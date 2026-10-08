using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Models;
using GrayMoon.App.Services.GitHub;
using GrayMoon.App.Services.WorkspaceManifest;
using GrayMoon.Application.WorkspaceManifest;
using GrayMoon.Common.Git;
using WorkspaceManifestModel = GrayMoon.Application.WorkspaceManifest.WorkspaceManifest;

namespace GrayMoon.App.Services.Application;

/// <summary>What a valid Workspace definition resolves to on this computer.</summary>
public sealed record RestoreDefinitionPlan(
    WorkspaceManifestModel Manifest,
    WorkspaceType Type,
    WorkspaceVersioningMode Versioning,
    WorkspaceCiProvider Ci,
    IReadOnlyList<int> SourceRepositoryIds,
    int RepositoryCount,
    int ConnectorCount,
    IReadOnlyList<string> MissingConnectors,
    IReadOnlyList<string> MissingRepositories);

/// <summary>One imported repository, as the evaluator needs it.</summary>
public sealed record RestoreCatalogEntry(int RepositoryId, string CloneUrl);

/// <summary>
/// Validates Workspace definition content and resolves it against the local connectors and imported repositories.
/// Pure: the same rules run for the preflight (content read through the connector) and after the clone (content read
/// from the new Workspace root), so the two can never disagree about what is valid.
/// </summary>
public static class RestoreDefinitionEvaluator
{
    public const string MissingDefinitionMessage = "This repository does not contain .graymoon.json.";

    public const string NewerSchemaMessage =
        "This Workspace definition was created by a newer GrayMoon version. Update GrayMoon before restoring it.";

    public static (RestoreDefinitionPlan? Plan, string? Error) Evaluate(
        string? content,
        string workspaceRepositoryCloneUrl,
        IReadOnlyList<Connector> localConnectors,
        IReadOnlyList<RestoreCatalogEntry> catalog)
    {
        if (content is null)
            return (null, MissingDefinitionMessage);

        if (WorkspaceManifestSerializer.IsNewerSchema(content))
            return (null, NewerSchemaMessage);

        if (!WorkspaceManifestSerializer.TryParse(content, out var manifest, out var parseError) || manifest is null)
            return (null, Invalid(parseError ?? "it could not be read"));

        var profile = manifest.Workspace.Profile;
        if (!WorkspaceManifestProfileNames.TryParse(profile.Type, out WorkspaceType type))
            return (null, UnknownProfileValue(profile.Type));
        if (!WorkspaceManifestProfileNames.TryParse(profile.Versioning, out WorkspaceVersioningMode versioning))
            return (null, UnknownProfileValue(profile.Versioning));
        if (!WorkspaceManifestProfileNames.TryParse(profile.Ci, out WorkspaceCiProvider ci))
            return (null, UnknownProfileValue(profile.Ci));

        if (manifest.Repositories.Any(r => string.IsNullOrWhiteSpace(r.RepositoryUrl)))
            return (null, Invalid("a repository entry has no repositoryUrl"));

        // Connectors (D11): nothing is fabricated for one that is not configured here.
        var connectorUrls = manifest.Connectors
            .Select(c => (c.Type, Url: RepositoryUrlIdentity.NormalizeConnectorUrl(c.Url)))
            .Where(c => c.Url.Length > 0)
            .DistinctBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var localConnectorUrls = localConnectors
            .Where(c => c.ConnectorType == ConnectorType.GitHub)
            .Select(c => RepositoryUrlIdentity.NormalizeConnectorUrl(RepositoryUrlHelper.GetWebRootFromConnectorApiBase(c.ApiBaseUrl)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingConnectors = connectorUrls
            .Where(c => !string.Equals(c.Type, "github", StringComparison.OrdinalIgnoreCase) || !localConnectorUrls.Contains(c.Url))
            .Select(c => ConnectorDisplayName(c.Url))
            .ToList();

        // Repositories (D14 step 6): by normalized URL among the imported catalog. The Workspace repository itself is
        // never a Source repository of its own Workspace, and a repository listed twice is counted once.
        var catalogByUrl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.OrderBy(e => e.RepositoryId))
            catalogByUrl.TryAdd(RepositoryUrlIdentity.NormalizeRepositoryUrl(entry.CloneUrl), entry.RepositoryId);

        var workspaceRepositoryKey = RepositoryUrlIdentity.NormalizeRepositoryUrl(workspaceRepositoryCloneUrl);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { workspaceRepositoryKey };
        var resolvedIds = new SortedSet<int>();
        var missingRepositories = new List<string>();
        var repositoryCount = 0;
        foreach (var repository in manifest.Repositories)
        {
            var key = RepositoryUrlIdentity.NormalizeRepositoryUrl(repository.RepositoryUrl);
            if (!seen.Add(key))
                continue;

            repositoryCount++;
            if (catalogByUrl.TryGetValue(key, out var resolvedId))
                resolvedIds.Add(resolvedId);
            else
                missingRepositories.Add(RepositoryDisplayName(repository));
        }

        return (new RestoreDefinitionPlan(
            manifest,
            type,
            versioning,
            ci,
            resolvedIds.ToList(),
            repositoryCount,
            connectorUrls.Count,
            missingConnectors,
            missingRepositories.Order(StringComparer.OrdinalIgnoreCase).ToList()), null);
    }

    /// <summary>"owner/name" from the repository URL when it has that shape; otherwise the name, then the URL.</summary>
    public static string RepositoryDisplayName(WorkspaceManifestRepository repository)
    {
        if (RepositoryUrlHelper.TryParseGitHubOwnerRepo(repository.RepositoryUrl, out var owner, out var name)
            && !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(name))
        {
            return $"{owner}/{name}";
        }

        return string.IsNullOrWhiteSpace(repository.Name) ? repository.RepositoryUrl : repository.Name;
    }

    /// <summary>The host of a connector URL ("github.com"); the URL itself when it has no host.</summary>
    public static string ConnectorDisplayName(string connectorUrl) =>
        Uri.TryCreate(connectorUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : connectorUrl;

    private static string Invalid(string detail) => $"The Workspace definition is invalid: {detail.TrimEnd('.')}.";

    private static string UnknownProfileValue(string? value) =>
        Invalid($"Unknown workspace profile value \"{value}\"");
}
