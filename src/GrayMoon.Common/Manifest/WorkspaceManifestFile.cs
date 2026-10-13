using System.Text.Json;
using GrayMoon.Common.Git;

namespace GrayMoon.Common.Manifest;

/// <inheritdoc cref="IWorkspaceManifestFile" />
public sealed class WorkspaceManifestFile : IWorkspaceManifestFile
{
    /// <summary>File name of the Workspace definition. Use <see cref="IWorkspaceManifestFile.FileName"/> where the service is at hand; this constant is for compile-time contexts such as messages.</summary>
    public const string FileName = "graymoon.json";

    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    string IWorkspaceManifestFile.FileName => FileName;

    public int CurrentSchemaVersion => SchemaVersion;

    public string Serialize(WorkspaceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var sorted = manifest with
        {
            Connectors = (manifest.Connectors ?? [])
                .OrderBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Repositories = (manifest.Repositories ?? [])
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        var json = JsonSerializer.Serialize(sorted, WriteOptions).Replace("\r\n", "\n");
        return json + "\n";
    }

    public bool TryParse(string? content, out WorkspaceManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;

        if (string.IsNullOrWhiteSpace(content))
        {
            error = "Workspace definition is empty";
            return false;
        }

        try
        {
            var schemaVersion = ReadSchemaVersion(content);
            if (schemaVersion > SchemaVersion)
            {
                error = $"Workspace definition schema {schemaVersion} is newer than this GrayMoon";
                return false;
            }

            var parsed = JsonSerializer.Deserialize<WorkspaceManifest>(content, ReadOptions);
            if (parsed is null)
            {
                error = "Workspace definition is empty";
                return false;
            }

            if (parsed.Workspace is null || string.IsNullOrWhiteSpace(parsed.Workspace.Name))
            {
                error = "Workspace definition is missing workspace.name";
                return false;
            }

            var profile = parsed.Workspace.Profile
                ?? new WorkspaceManifestProfile("basic", "none", "none");

            var repositories = new List<WorkspaceManifestRepository>();
            foreach (var repository in parsed.Repositories ?? [])
            {
                if (repository is null)
                    continue;

                if (!WorkspaceDefinitionTagPin.TryNormalize(
                        repository.Tag,
                        repository.Commit,
                        repository.Name,
                        out var tag,
                        out var commit,
                        out var pinError))
                {
                    error = pinError;
                    return false;
                }

                repositories.Add(new WorkspaceManifestRepository(
                    repository.Name ?? string.Empty,
                    repository.RepositoryUrl ?? string.Empty,
                    repository.ConnectorUrl ?? string.Empty,
                    tag,
                    commit));
            }

            manifest = new WorkspaceManifest(
                schemaVersion,
                new WorkspaceManifestWorkspace(
                    parsed.Workspace.Name,
                    new WorkspaceManifestProfile(
                        profile.Type ?? "basic",
                        profile.Versioning ?? "none",
                        profile.Ci ?? "none")),
                (parsed.Connectors ?? [])
                    .Where(c => c is not null)
                    .Select(c => new WorkspaceManifestConnector(c.Type ?? string.Empty, c.Url ?? string.Empty))
                    .ToList(),
                repositories,
                parsed.CodeGraph);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Workspace definition is not valid JSON: {ex.Message}";
            return false;
        }
    }

    public bool IsNewerSchema(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        try
        {
            return ReadSchemaVersion(content) > SchemaVersion;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public (string? Commit, string? Error) ReadTagPinCommit(string? content, string? repositoryUrl)
    {
        if (content is null)
            return (null, null);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            return (null, $"Workspace definition is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, "Workspace definition is not valid JSON: the root element must be an object.");

            if (!TryProperty(document.RootElement, "repositories", out var repositories)
                || repositories.ValueKind == JsonValueKind.Null)
                return (null, null);

            if (repositories.ValueKind != JsonValueKind.Array)
                return (null, "Workspace definition repositories must be an array.");

            foreach (var repository in repositories.EnumerateArray())
            {
                if (repository.ValueKind != JsonValueKind.Object)
                    return (null, "Workspace definition repositories must be objects.");

                if (!TryProperty(repository, "repositoryUrl", out var urlElement)
                    || urlElement.ValueKind != JsonValueKind.String
                    || !RepositoryUrlIdentity.RepositoryUrlsEqual(urlElement.GetString(), repositoryUrl))
                    continue;

                var name = TryProperty(repository, "name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;

                if (!TryReadString(repository, "tag", name, out var tag, out var tagError))
                    return (null, tagError);
                if (!TryReadString(repository, "commit", name, out var commit, out var commitError))
                    return (null, commitError);
                if (!WorkspaceDefinitionTagPin.TryNormalize(tag, commit, name, out _, out var normalizedCommit, out var pinError))
                    return (null, pinError);

                return (normalizedCommit, null);
            }

            return (null, null);
        }
    }

    public IReadOnlyList<string> ReadRepositoryFolderNames(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !TryProperty(document.RootElement, "repositories", out var repositories)
                || repositories.ValueKind != JsonValueKind.Array)
                return [];

            var names = new List<string>();
            foreach (var repository in repositories.EnumerateArray())
            {
                if (repository.ValueKind != JsonValueKind.Object
                    || !TryProperty(repository, "name", out var nameElement)
                    || nameElement.ValueKind != JsonValueKind.String)
                    continue;

                var name = nameElement.GetString()?.Trim();
                if (!string.IsNullOrEmpty(name) && IsSingleFolderName(name)
                    && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
            }

            return names;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<string?> ReadFromDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(directory, FileName);
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken)
            : null;
    }

    public IReadOnlyList<string> ReadRepositoryFolderNamesFromDirectory(string directory)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            return File.Exists(path) ? ReadRepositoryFolderNames(File.ReadAllText(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static int ReadSchemaVersion(string content)
    {
        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Root element must be an object.");

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, "version", StringComparison.OrdinalIgnoreCase))
                continue;
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var version))
                return version;
            throw new JsonException("version must be an integer.");
        }

        return SchemaVersion;
    }

    // A name that is not one plain folder name (".", "..", or one with a separator on any platform) can never be a sibling folder.
    private static bool IsSingleFolderName(string name)
        => name is not "." and not ".."
           && name.IndexOfAny(['/', '\\']) < 0;

    private static bool TryReadString(JsonElement repository, string propertyName, string? repositoryName, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (!TryProperty(repository, propertyName, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;

        if (element.ValueKind != JsonValueKind.String)
        {
            var label = string.IsNullOrWhiteSpace(repositoryName) ? "A repository" : $"Repository '{repositoryName}'";
            error = $"{label} {propertyName} must be a string.";
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
