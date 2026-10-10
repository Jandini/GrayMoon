using System.Text.Json;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Services.WorkspaceManifest;

using WorkspaceManifest = GrayMoon.Application.WorkspaceManifest.WorkspaceManifest;
using WorkspaceManifestConnector = GrayMoon.Application.WorkspaceManifest.WorkspaceManifestConnector;
using WorkspaceManifestProfile = GrayMoon.Application.WorkspaceManifest.WorkspaceManifestProfile;
using WorkspaceManifestRepository = GrayMoon.Application.WorkspaceManifest.WorkspaceManifestRepository;
using WorkspaceManifestWorkspace = GrayMoon.Application.WorkspaceManifest.WorkspaceManifestWorkspace;

/// <summary>
/// Reads and writes the Workspace definition file (schema v1, D15). Output is byte-stable: fixed
/// property order, sorted arrays, LF line endings and a trailing newline.
/// </summary>
public static class WorkspaceManifestSerializer
{
    public const int CurrentSchemaVersion = 1;

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

    public static string Serialize(WorkspaceManifest manifest)
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

    public static bool TryParse(string content, out WorkspaceManifest? manifest, out string? error)
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
            if (schemaVersion > CurrentSchemaVersion)
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
                    manifest = null;
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

    /// <summary>True when the content is JSON whose <c>version</c> is newer than this GrayMoon understands.</summary>
    public static bool IsNewerSchema(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        try
        {
            return ReadSchemaVersion(content) > CurrentSchemaVersion;
        }
        catch (JsonException)
        {
            return false;
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

        return CurrentSchemaVersion;
    }
}
