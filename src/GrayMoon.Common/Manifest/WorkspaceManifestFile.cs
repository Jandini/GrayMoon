using System.Text.Json;
using GrayMoon.Common.Git;

namespace GrayMoon.Common.Manifest;

/// <inheritdoc cref="IWorkspaceManifestFile" />
public sealed class WorkspaceManifestFile : IWorkspaceManifestFile
{
    /// <summary>File name of the Workspace definition. Use <see cref="IWorkspaceManifestFile.FileName"/> where the service is at hand; this constant is for compile-time contexts such as messages.</summary>
    public const string FileName = "graymoon.json";

    /// <summary>The version of a definition that records no read-only state. Every GrayMoon reads it.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// The newest version this GrayMoon reads, and the one it writes when the definition holds <c>reference</c> or
    /// <c>branch</c>. An older GrayMoon refuses such a file by its version rather than misreading a commit without a tag.
    /// </summary>
    public const int MaxSchemaVersion = 2;

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

    public int CurrentSchemaVersion => MaxSchemaVersion;

    public string Serialize(WorkspaceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var sorted = manifest with
        {
            // The content decides the version, not the caller: a definition without read-only data stays readable
            // by every GrayMoon.
            SchemaVersion = UsesReferenceData(manifest) ? MaxSchemaVersion : SchemaVersion,
            Connectors = (manifest.Connectors ?? [])
                .OrderBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Repositories = (manifest.Repositories ?? [])
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            VersionFiles = manifest.VersionFiles is { Count: > 0 } versionFiles
                ? versionFiles
                    .OrderBy(f => f.Repository, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : null
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
            if (schemaVersion > MaxSchemaVersion)
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

            if (parsed.Workspace is null)
            {
                error = "Workspace definition is missing workspace";
                return false;
            }

            if (schemaVersion < MaxSchemaVersion && UsesReferenceData(parsed))
            {
                error = $"Workspace definition uses reference or branch, which needs \"version\": {MaxSchemaVersion}";
                return false;
            }

            var profile = parsed.Workspace.Profile
                ?? new WorkspaceManifestProfile("basic", "none", "none");
            var referenceDefault = parsed.Workspace.Reference;

            var repositories = new List<WorkspaceManifestRepository>();
            foreach (var repository in parsed.Repositories ?? [])
            {
                if (repository is null)
                    continue;

                // An entry without its own reference takes the workspace value (a file written before per-repository
                // state, or edited by hand).
                if (!WorkspaceDefinitionRevisionPin.TryNormalize(
                        repository.Tag,
                        repository.Branch,
                        repository.Commit,
                        repository.Reference ?? referenceDefault ?? false,
                        repository.Name,
                        out var tag,
                        out var branch,
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
                    commit,
                    repository.Reference,
                    branch));
            }

            if (!TryNormalizeVersionFiles(parsed.VersionFiles, out var versionFiles, out var versionFileError))
            {
                error = versionFileError;
                return false;
            }

            if (!TryNormalizeAi(parsed.Ai, out var ai, out var aiError))
            {
                error = aiError;
                return false;
            }

            manifest = new WorkspaceManifest(
                schemaVersion,
                new WorkspaceManifestWorkspace(
                    new WorkspaceManifestProfile(
                        profile.Type ?? "basic",
                        profile.Versioning ?? "none",
                        profile.Ci ?? "none"),
                    referenceDefault),
                (parsed.Connectors ?? [])
                    .Where(c => c is not null)
                    .Select(c => new WorkspaceManifestConnector(c.Type ?? string.Empty, c.Url ?? string.Empty))
                    .ToList(),
                repositories,
                parsed.CodeGraph,
                versionFiles,
                ai);
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
            return ReadSchemaVersion(content) > MaxSchemaVersion;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public (string? Commit, string? Branch, string? Error) ReadPinnedRevision(string? content, string? repositoryUrl)
    {
        if (content is null)
            return (null, null, null);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            return (null, null, $"Workspace definition is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null, "Workspace definition is not valid JSON: the root element must be an object.");

            if (!TryProperty(document.RootElement, "repositories", out var repositories)
                || repositories.ValueKind == JsonValueKind.Null)
                return (null, null, null);

            if (repositories.ValueKind != JsonValueKind.Array)
                return (null, null, "Workspace definition repositories must be an array.");

            foreach (var repository in repositories.EnumerateArray())
            {
                if (repository.ValueKind != JsonValueKind.Object)
                    return (null, null, "Workspace definition repositories must be objects.");

                if (!TryProperty(repository, "repositoryUrl", out var urlElement)
                    || urlElement.ValueKind != JsonValueKind.String
                    || !RepositoryUrlIdentity.RepositoryUrlsEqual(urlElement.GetString(), repositoryUrl))
                    continue;

                var name = TryProperty(repository, "name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;

                if (!TryReadString(repository, "tag", name, out var tag, out var tagError))
                    return (null, null, tagError);
                if (!TryReadString(repository, "branch", name, out var branch, out var branchError))
                    return (null, null, branchError);
                if (!TryReadString(repository, "commit", name, out var commit, out var commitError))
                    return (null, null, commitError);
                if (!TryReadReference(repository, name, out var reference, out var referenceError))
                    return (null, null, referenceError);
                if (reference is null && branch is not null)
                {
                    // The entry has no reference of its own: the workspace value applies, as in TryParse.
                    if (!TryReadWorkspaceReference(document.RootElement, out reference, out var defaultError))
                        return (null, null, defaultError);
                }

                if (!WorkspaceDefinitionRevisionPin.TryNormalize(
                        tag, branch, commit, reference ?? false, name, out _, out var normalizedBranch, out var normalizedCommit, out var pinError))
                    return (null, null, pinError);

                return (normalizedCommit, normalizedBranch, null);
            }

            return (null, null, null);
        }
    }

    private static bool UsesReferenceData(WorkspaceManifest manifest) =>
        manifest.Workspace?.Reference is not null
        || (manifest.Repositories ?? []).Any(repository =>
            repository is not null && (repository.Reference is not null || !string.IsNullOrWhiteSpace(repository.Branch)));

    private static bool TryReadWorkspaceReference(JsonElement root, out bool? reference, out string? error)
    {
        reference = null;
        error = null;
        return !TryProperty(root, "workspace", out var workspace)
            || workspace.ValueKind != JsonValueKind.Object
            || TryReadReference(workspace, null, out reference, out error, "Workspace definition workspace.reference");
    }

    /// <summary>The same lenient boolean as <see cref="ReferenceBooleanJsonConverter"/>, read from an element.</summary>
    private static bool TryReadReference(JsonElement owner, string? repositoryName, out bool? reference, out string? error, string? label = null)
    {
        reference = null;
        error = null;
        if (!TryProperty(owner, "reference", out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                reference = true;
                return true;
            case JsonValueKind.False:
                reference = false;
                return true;
            case JsonValueKind.String when bool.TryParse(element.GetString()?.Trim(), out var value):
                reference = value;
                return true;
        }

        label ??= string.IsNullOrWhiteSpace(repositoryName) ? "A repository reference" : $"Repository '{repositoryName}' reference";
        error = $"{label} must be true or false.";
        return false;
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

    /// <summary>
    /// Validates the <c>versionFiles</c> entries: each needs a repository and a path. Paths use <c>/</c>, pattern lines
    /// are normalized, and an entry listed twice (same repository and path) keeps its last occurrence.
    /// </summary>
    private static bool TryNormalizeVersionFiles(
        IReadOnlyList<WorkspaceManifestVersionFile>? entries,
        out IReadOnlyList<WorkspaceManifestVersionFile>? versionFiles,
        out string? error)
    {
        versionFiles = null;
        error = null;
        if (entries is null)
            return true;

        var byKey = new Dictionary<string, WorkspaceManifestVersionFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry is null)
                continue;

            var repository = entry.Repository?.Trim() ?? string.Empty;
            var path = WorkspaceManifestVersionFile.NormalizePath(entry.Path);
            if (repository.Length == 0)
            {
                error = "A version file entry has no repository";
                return false;
            }

            if (path.Length == 0)
            {
                error = $"A version file entry of repository '{repository}' has no path";
                return false;
            }

            var pattern = WorkspaceManifestVersionFile.SplitPattern(string.Join("\n", (entry.Pattern ?? []).Where(l => l is not null)));
            byKey[$"{repository}\n{path}"] = new WorkspaceManifestVersionFile(repository, path, pattern);
        }

        versionFiles = byKey.Values.ToList();
        return true;
    }

    /// <summary>
    /// Shapes the <c>ai</c> section: every provider entry needs a <c>provider</c> id. Ids and models are trimmed and an
    /// empty model becomes null (the provider default). Entries keep their order and are never dropped, not even unknown or
    /// duplicate ids: those are reported where the configuration is used, so a rewrite keeps what the user wrote.
    /// </summary>
    private static bool TryNormalizeAi(WorkspaceManifestAi? section, out WorkspaceManifestAi? ai, out string? error)
    {
        ai = null;
        error = null;
        if (section is null)
            return true;

        var providers = new List<WorkspaceManifestAiProvider>();
        foreach (var entry in section.Providers ?? [])
        {
            if (entry is null)
                continue;

            var provider = entry.Provider?.Trim() ?? string.Empty;
            if (provider.Length == 0)
            {
                error = "An ai.providers entry has no provider";
                return false;
            }

            var model = string.IsNullOrWhiteSpace(entry.Model) ? null : entry.Model.Trim();
            providers.Add(new WorkspaceManifestAiProvider(provider, model));
        }

        ai = new WorkspaceManifestAi(providers);
        return true;
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
