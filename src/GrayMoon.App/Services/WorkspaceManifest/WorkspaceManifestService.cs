using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services.GitHub;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.WorkspaceManifest;

using IWorkspaceManifestService = GrayMoon.Application.WorkspaceManifest.IWorkspaceManifestService;
using WorkspaceManifest = GrayMoon.Application.WorkspaceManifest.WorkspaceManifest;
using WorkspaceManifestConnector = GrayMoon.Application.WorkspaceManifest.WorkspaceManifestConnector;
using WorkspaceManifestDrift = GrayMoon.Application.WorkspaceManifest.WorkspaceManifestDrift;
using WorkspaceManifestProfile = GrayMoon.Application.WorkspaceManifest.WorkspaceManifestProfile;
using WorkspaceManifestRepository = GrayMoon.Application.WorkspaceManifest.WorkspaceManifestRepository;
using WorkspaceManifestWorkspace = GrayMoon.Application.WorkspaceManifest.WorkspaceManifestWorkspace;

/// <summary>
/// Builds, writes and compares the Workspace definition (<c>.graymoon.json</c>) and the managed <c>.gitignore</c>
/// section of the Workspace repository (D5, D8, D11, D12). Uses factory-created contexts, so it is safe to run
/// outside the circuit's DbContext.
/// </summary>
public sealed class WorkspaceManifestService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IWorkerBridge workerBridge,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceFeatureContextResolver contextResolver,
    ILogger<WorkspaceManifestService> logger) : IWorkspaceManifestService
{
    internal const string NoWorkspaceRepositoryMessage = "No Workspace repository";

    public async Task<WorkspaceManifest> BuildFromDatabaseAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var workspace = await db.Workspaces
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, cancellationToken)
            ?? throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var sourceLinks = await db.WorkspaceRepositories
            .AsNoTracking()
            .Include(l => l.Repository)
            .ThenInclude(r => r!.Connector)
            .Where(l => l.WorkspaceId == workspaceId && l.Role == WorkspaceRepositoryRole.Source)
            .ToListAsync(cancellationToken);
        var sourceRepositories = sourceLinks
            .Where(l => l.Repository is not null)
            .Select(l => l.Repository!)
            .ToList();

        var connectors = sourceRepositories
            .Select(r => r.Connector)
            .Where(c => c is { ConnectorType: ConnectorType.GitHub })
            .Select(c => ConnectorUrl(c))
            .Where(url => url.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(url => url, StringComparer.OrdinalIgnoreCase)
            .Select(url => new WorkspaceManifestConnector("github", url))
            .ToList();

        var repositories = sourceRepositories
            .Select(r => new WorkspaceManifestRepository(r.RepositoryName, r.CloneUrl, ConnectorUrl(r.Connector)))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WorkspaceManifest(
            WorkspaceManifestSerializer.CurrentSchemaVersion,
            new WorkspaceManifestWorkspace(
                workspace.Name,
                new WorkspaceManifestProfile(
                    WorkspaceManifestProfileNames.ToManifest(workspace.Type),
                    WorkspaceManifestProfileNames.ToManifest(workspace.VersioningMode),
                    WorkspaceManifestProfileNames.ToManifest(workspace.CiProvider))),
            connectors,
            repositories);
    }

    public string Serialize(WorkspaceManifest manifest) => WorkspaceManifestSerializer.Serialize(manifest);

    public bool TryParse(string content, out WorkspaceManifest? manifest, out string? error) =>
        WorkspaceManifestSerializer.TryParse(content, out manifest, out error);

    public async Task<OperationResult> WriteAuthoritativeManifestAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var (_, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);
            if (args.WorkspaceRepositoryName is null)
                return new OperationResult(true, NoWorkspaceRepositoryMessage);

            var manifest = await BuildFromDatabaseAsync(workspaceId, cancellationToken);
            var content = Serialize(manifest);
            var existing = await WorkspaceRepositoryFileAccess.ReadAsync(
                workerBridge, args, WorkspaceRepositoryFileAccess.ManifestFilePath, cancellationToken);
            if (existing.Found)
                content = WorkspaceManifestRecentTools.Preserve(content, existing.Content);
            return await WorkspaceRepositoryFileAccess.WriteAsync(
                workerBridge, args, WorkspaceRepositoryFileAccess.ManifestFilePath, content, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Writing the Workspace definition failed. WorkspaceId={WorkspaceId}", workspaceId);
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<OperationResult> WriteManagedGitIgnoreAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var (_, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);
            if (args.WorkspaceRepositoryName is null)
                return new OperationResult(true, NoWorkspaceRepositoryMessage);

            var read = await WorkspaceRepositoryFileAccess.ReadAsync(
                workerBridge, args, WorkspaceRepositoryFileAccess.GitIgnoreFilePath, cancellationToken);
            if (read.Error is not null)
                return OperationResult.Fail(read.Error);

            var manifest = await BuildFromDatabaseAsync(workspaceId, cancellationToken);
            var content = ManagedGitIgnoreSection.Apply(read.Content, manifest.Repositories.Select(r => r.Name));
            return await WorkspaceRepositoryFileAccess.WriteAsync(
                workerBridge, args, WorkspaceRepositoryFileAccess.GitIgnoreFilePath, content, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Writing the managed .gitignore section failed. WorkspaceId={WorkspaceId}", workspaceId);
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<WorkspaceManifestDrift> DetectDriftAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var (contextId, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);

        // D8 point 4: Feature contexts never run drift detection.
        var info = await contextResolver.GetRequiredAsync(contextId, workspaceId, cancellationToken);
        if (!info.IsSpecialWorkspace)
            throw new InvalidOperationException("Workspace definition drift is only detected for the special Workspace context.");

        if (args.WorkspaceRepositoryName is null)
        {
            await PersistDriftAsync(workspaceId, hasDrift: false, cancellationToken);
            return NoDrift();
        }

        var read = await WorkspaceRepositoryFileAccess.ReadAsync(
            workerBridge, args, WorkspaceRepositoryFileAccess.ManifestFilePath, cancellationToken);
        if (read.Error is not null)
            throw new InvalidOperationException(read.Error);

        WorkspaceManifestDrift drift;
        if (!read.Found)
        {
            drift = ParseFailure($"{WorkspaceRepositoryFileAccess.ManifestFilePath} was not found in the Workspace repository");
        }
        else if (!TryParse(read.Content ?? string.Empty, out var fileManifest, out var parseError) || fileManifest is null)
        {
            drift = ParseFailure(parseError ?? "Workspace definition could not be read");
        }
        else
        {
            var databaseManifest = await BuildFromDatabaseAsync(workspaceId, cancellationToken);
            drift = Compare(fileManifest, databaseManifest);
        }

        await PersistDriftAsync(workspaceId, drift.HasDrift, cancellationToken);
        return drift;
    }

    private static WorkspaceManifestDrift Compare(WorkspaceManifest file, WorkspaceManifest database)
    {
        var added = new List<string>();
        var removed = new List<string>();

        var databaseByUrl = ByRepositoryUrl(database.Repositories);
        var fileByUrl = ByRepositoryUrl(file.Repositories);
        foreach (var (url, repository) in fileByUrl.OrderBy(p => p.Value.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!databaseByUrl.ContainsKey(url))
                added.Add(repository.Name);
        }

        foreach (var (url, repository) in databaseByUrl.OrderBy(p => p.Value.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!fileByUrl.ContainsKey(url))
                removed.Add(repository.Name);
        }

        var fileConnectors = NormalizedConnectorUrls(file.Connectors);
        var databaseConnectors = NormalizedConnectorUrls(database.Connectors);
        var addedConnectors = fileConnectors.Except(databaseConnectors, StringComparer.OrdinalIgnoreCase).ToList();
        var removedConnectors = databaseConnectors.Except(fileConnectors, StringComparer.OrdinalIgnoreCase).ToList();

        var changedProfile = new List<string>();
        var fileProfile = file.Workspace.Profile;
        var databaseProfile = database.Workspace.Profile;
        if (!string.Equals(fileProfile.Type, databaseProfile.Type, StringComparison.OrdinalIgnoreCase))
            changedProfile.Add("type");
        if (!string.Equals(fileProfile.Versioning, databaseProfile.Versioning, StringComparison.OrdinalIgnoreCase))
            changedProfile.Add("versioning");
        if (!string.Equals(fileProfile.Ci, databaseProfile.Ci, StringComparison.OrdinalIgnoreCase))
            changedProfile.Add("ci");

        var hasDrift = added.Count > 0 || removed.Count > 0 || changedProfile.Count > 0
            || addedConnectors.Count > 0 || removedConnectors.Count > 0;
        return new WorkspaceManifestDrift(hasDrift, null, added, removed, changedProfile, addedConnectors, removedConnectors);
    }

    private static Dictionary<string, WorkspaceManifestRepository> ByRepositoryUrl(IReadOnlyList<WorkspaceManifestRepository> repositories)
    {
        var result = new Dictionary<string, WorkspaceManifestRepository>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in repositories)
            result.TryAdd(RepositoryUrlIdentity.NormalizeRepositoryUrl(repository.RepositoryUrl), repository);
        return result;
    }

    private static List<string> NormalizedConnectorUrls(IReadOnlyList<WorkspaceManifestConnector> connectors) =>
        connectors
            .Select(c => RepositoryUrlIdentity.NormalizeConnectorUrl(c.Url))
            .Where(url => url.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(url => url, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static WorkspaceManifestDrift NoDrift() => new(false, null, [], [], [], [], []);

    private static WorkspaceManifestDrift ParseFailure(string reason) => new(true, reason, [], [], [], [], []);

    private async Task PersistDriftAsync(int workspaceId, bool hasDrift, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, cancellationToken);
        if (workspace is null)
            return;

        workspace.ManifestDriftDetectedAt = hasDrift ? DateTime.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<(WorkspaceFeatureContextId ContextId, WorkerWorkspaceArgs Args)> GetSpecialContextArgsAsync(
        int workspaceId,
        CancellationToken cancellationToken)
    {
        var contextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        var args = await pathResolver.GetWorkerArgsAsync(contextId, cancellationToken);
        return (contextId, args);
    }

    private static string ConnectorUrl(Connector? connector) =>
        RepositoryUrlIdentity.NormalizeConnectorUrl(
            RepositoryUrlHelper.GetWebRootFromConnectorApiBase(connector?.ApiBaseUrl));
}

/// <summary>
/// Worker file access for the Workspace repository root, shared by <see cref="WorkspaceManifestService"/> and the
/// Workspace-repository operations. Reads use <c>GetFileContents</c>, writes use <c>WriteRepositoryFile</c> (D5).
/// </summary>
internal static class WorkspaceRepositoryFileAccess
{
    public const string ManifestFilePath = ".graymoon.json";
    public const string GitIgnoreFilePath = ".gitignore";

    private const string NotFoundPrefix = "File not found";

    /// <summary>
    /// Reads a file of the Workspace repository. <c>Found</c> is false for a missing file (no error); any other
    /// failure is reported in <c>Error</c>.
    /// </summary>
    public static async Task<(bool Found, string? Content, string? Error)> ReadAsync(
        IWorkerBridge workerBridge,
        WorkerWorkspaceArgs args,
        string filePath,
        CancellationToken cancellationToken)
    {
        var response = await workerBridge.SendCommandAsync(
            "GetFileContents",
            new
            {
                workspaceName = args.WorkspaceFolderName,
                workspaceRoot = args.WorkspaceRoot,
                workspaceRepositoryName = args.WorkspaceRepositoryName,
                repositoryName = args.WorkspaceRepositoryName,
                filePath,
            },
            cancellationToken);

        if (!response.Success)
            return (false, null, response.Error ?? $"Could not read {filePath}.");

        var data = WorkerResponseJson.DeserializeWorkerResponse<WorkerGetFileContentsResponse>(response.Data);
        if (data is null)
            return (false, null, $"The Worker returned an unreadable response for {filePath}.");

        if (!string.IsNullOrWhiteSpace(data.ErrorMessage))
        {
            return data.ErrorMessage.StartsWith(NotFoundPrefix, StringComparison.OrdinalIgnoreCase)
                ? (false, null, null)
                : (false, null, data.ErrorMessage);
        }

        return (true, data.Content ?? string.Empty, null);
    }

    public static async Task<OperationResult> WriteAsync(
        IWorkerBridge workerBridge,
        WorkerWorkspaceArgs args,
        string filePath,
        string content,
        CancellationToken cancellationToken)
    {
        var workspaceRepositoryName = args.WorkspaceRepositoryName
            ?? throw new InvalidOperationException("The Workspace has no Workspace repository.");

        var response = await workerBridge.SendCommandAsync(
            WorkerHubMethods.WriteRepositoryFile,
            new
            {
                workspaceName = args.WorkspaceFolderName,
                workspaceRoot = args.WorkspaceRoot,
                workspaceRepositoryName,
                repositoryName = workspaceRepositoryName,
                filePath,
                content,
                onlyIfChanged = true,
            },
            cancellationToken);

        if (!response.Success)
            return OperationResult.Fail(response.Error ?? $"Could not write {filePath}.");

        var data = WorkerResponseJson.DeserializeWorkerResponse<WriteRepositoryFileWorkerResponse>(response.Data);
        if (data is { Success: false })
            return OperationResult.Fail(data.ErrorMessage ?? $"Could not write {filePath}.");

        return OperationResult.Ok();
    }

    internal sealed class WriteRepositoryFileWorkerResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("success")]
        public bool Success { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("written")]
        public bool Written { get; set; }
    }
}

/// <summary>Call-site helpers for the membership, profile and drift hooks (plan Unit D steps 6 and 7).</summary>
public static class WorkspaceManifestHooks
{
    /// <summary>
    /// Regenerates the managed <c>.gitignore</c> section and then the Workspace definition. Does nothing when the
    /// Workspace has no Workspace repository. Never throws; returns the first failure message for the caller to
    /// show as a toast, or null when everything was written (or there was nothing to write).
    /// </summary>
    public static async Task<string?> SyncDefinitionToDiskAsync(
        this IWorkspaceManifestService manifestService,
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var gitIgnore = await manifestService.WriteManagedGitIgnoreAsync(workspaceId, cancellationToken);
            if (!gitIgnore.Success)
                return $"Could not update .gitignore in the Workspace repository: {gitIgnore.Error}";

            var manifest = await manifestService.WriteAuthoritativeManifestAsync(workspaceId, cancellationToken);
            if (!manifest.Success)
                return $"Could not write the Workspace definition: {manifest.Error}";

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Could not write the Workspace definition: {ex.Message}";
        }
    }

    /// <summary>
    /// Fire-and-forget drift detection with its own DI scope (AGENTS.md rule 3), so it never reuses a circuit's
    /// DbContext and never outlives a disposed scope. Failures are logged and swallowed.
    /// </summary>
    public static void DetectDriftInBackground(IServiceScopeFactory scopeFactory, ILogger logger, int workspaceId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var manifestService = scope.ServiceProvider.GetRequiredService<IWorkspaceManifestService>();
                await manifestService.DetectDriftAsync(workspaceId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Workspace definition drift detection failed. WorkspaceId={WorkspaceId}", workspaceId);
            }
        });
    }
}
