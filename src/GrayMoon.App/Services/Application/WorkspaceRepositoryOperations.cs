using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.GitHub;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.WorkspaceManifest;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Application.WorkspaceManifest;
using GrayMoon.Application.Workspaces;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;
using WorkspaceEntity = GrayMoon.App.Models.Workspace;
using WorkspaceRepositoryEntity = GrayMoon.App.Repositories.WorkspaceRepository;

namespace GrayMoon.App.Services.Application;

/// <summary>
/// Enable, disable and restore of the Workspace repository (D3, D4, D14). Enabling runs under the Workspace's
/// structural lock; every database change goes through factory-created contexts.
/// </summary>
public sealed class WorkspaceRepositoryOperations(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IWorkerBridge workerBridge,
    IWorkerFeatureSupportService featureSupport,
    IWorkspaceManifestService manifestService,
    IWorkspaceOperationLock operationLock,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceFeatureContextResolver contextResolver,
    WorkspaceRepositoryEntity workspaceRepository,
    ILogger<WorkspaceRepositoryOperations> logger) : IWorkspaceRepositoryOperations
{
    internal const string UnsupportedWorkerMessage =
        "The connected Worker does not support Workspace repositories. Update the Worker and try again.";

    internal const string FeaturesExistOnEnableMessage =
        "Cannot enable a Workspace repository while Features exist. Remove Features first.";

    internal const string FeaturesExistOnDisableMessage =
        "Cannot disable the Workspace repository while Features exist. Remove Features first.";

    public async Task<OperationResult> EnableWorkspaceRepositoryAsync(
        int workspaceId,
        int repositoryId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = operationLock.TryStartStructural(
            workspaceId,
            "enable-workspace-repository",
            WorkspaceJobKeys.RepositoriesOverlayKey(workspaceId),
            "Enabling Workspace repository...",
            async (op, ct) =>
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                    var report = BindProgress(op, progress);
                    tcs.TrySetResult(await EnableCoreAsync(workspaceId, repositoryId, report, linked.Token));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Enable Workspace repository threw. WorkspaceId={WorkspaceId} RepositoryId={RepositoryId}", workspaceId, repositoryId);
                    tcs.TrySetResult(OperationResult.Fail(ex.Message));
                }
            },
            out var operation);

        if (!started)
            return OperationResult.Fail("A Workspace structural operation is already running.");

        var result = await tcs.Task.WaitAsync(cancellationToken);
        await operation.WhenCompleted;
        return result;
    }

    public async Task<OperationResult> DisableWorkspaceRepositoryAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (await db.WorkspaceFeatures.AnyAsync(f => f.WorkspaceId == workspaceId, cancellationToken))
            return OperationResult.Fail(FeaturesExistOnDisableMessage);

        var link = await db.WorkspaceRepositories
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.WorkspaceId == workspaceId && l.Role == WorkspaceRepositoryRole.Workspace, cancellationToken);
        if (link is null)
            return OperationResult.Fail("This Workspace has no Workspace repository.");

        // Database only: the files and .git on disk are never touched.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await WorkspaceRepositoryLinkCleanup.DeleteDependentsAsync(
            db, [link.WorkspaceRepositoryId], [link.RepositoryId], workspaceId, cancellationToken);
        await db.WorkspaceRepositories
            .Where(l => l.WorkspaceRepositoryId == link.WorkspaceRepositoryId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.Workspaces
            .Where(w => w.WorkspaceId == workspaceId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.ManifestDriftDetectedAt, (DateTime?)null), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Persistence: removed Workspace-role link. WorkspaceId={WorkspaceId} RepositoryId={RepositoryId}",
            workspaceId, link.RepositoryId);
        return OperationResult.Ok();
    }

    public async Task<RestoreWorkspaceResult> RestoreFromRepositoryAsync(
        int repositoryId,
        string workspaceName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RestoreCoreAsync(repositoryId, workspaceName, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Restore from Workspace repository threw. RepositoryId={RepositoryId}", repositoryId);
            return RestoreFailure(ex.Message);
        }
    }

    private async Task<OperationResult> EnableCoreAsync(
        int workspaceId,
        int repositoryId,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        // (a) compatibility gate (D2)
        if (!await featureSupport.SupportsAsync(WorkerFeatures.WorkspaceRepository, cancellationToken))
            return OperationResult.Fail(UnsupportedWorkerMessage);

        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            if (!await db.Workspaces.AnyAsync(w => w.WorkspaceId == workspaceId, cancellationToken))
                return OperationResult.Fail("Workspace not found.");

            // (b)
            if (await db.WorkspaceFeatures.AnyAsync(f => f.WorkspaceId == workspaceId, cancellationToken))
                return OperationResult.Fail(FeaturesExistOnEnableMessage);

            // (c)
            if (await db.WorkspaceRepositories.AnyAsync(
                    l => l.WorkspaceId == workspaceId && l.Role == WorkspaceRepositoryRole.Workspace, cancellationToken))
                return OperationResult.Fail("This Workspace already has a Workspace repository.");

            // (d)
            if (await db.WorkspaceRepositories.AnyAsync(
                    l => l.WorkspaceId == workspaceId && l.RepositoryId == repositoryId, cancellationToken))
                return OperationResult.Fail("This repository is already a Source repository of this Workspace.");

            if (!await db.Repositories.AnyAsync(r => r.RepositoryId == repositoryId, cancellationToken))
                return OperationResult.Fail("Repository not found.");
        }

        // (e) + (f), with rollback of the link on failure
        progress.Report("Linking Workspace repository...");
        var attach = await LinkAndAttachAsync(workspaceId, repositoryId, progress, cancellationToken);
        if (!attach.Success)
            return OperationResult.Fail(attach.Error ?? "Attaching the Workspace repository failed.");

        // (g) .gitignore first, then the Workspace definition. The link and the attached repository stay in place on
        // failure: the drift banner offers "Write Workspace definition to disk" to repair the definition later.
        progress.Report("Writing .gitignore...");
        var gitIgnore = await manifestService.WriteManagedGitIgnoreAsync(workspaceId, cancellationToken);
        if (!gitIgnore.Success)
        {
            return OperationResult.Fail(
                $"The Workspace repository was attached, but .gitignore could not be updated: {gitIgnore.Error}");
        }

        progress.Report("Writing Workspace definition...");
        var manifest = await manifestService.WriteAuthoritativeManifestAsync(workspaceId, cancellationToken);
        if (!manifest.Success)
        {
            return OperationResult.Fail(
                $"The Workspace repository was attached, but the Workspace definition could not be written: {manifest.Error}");
        }

        // (h)
        return OperationResult.Ok();
    }

    private async Task<RestoreWorkspaceResult> RestoreCoreAsync(
        int repositoryId,
        string workspaceName,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        // D14 step 1
        if (string.IsNullOrWhiteSpace(workspaceName))
            return RestoreFailure("Workspace name is required.");

        if (!await featureSupport.SupportsAsync(WorkerFeatures.WorkspaceRepository, cancellationToken))
            return RestoreFailure(UnsupportedWorkerMessage);

        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            if (!await db.Repositories.AnyAsync(r => r.RepositoryId == repositoryId, cancellationToken))
                return RestoreFailure("Repository not found.");
        }

        // D14 step 2: Workspace row (Basic / None / None) + Workspace-role link, then attach.
        progress.Report("Creating Workspace...");
        WorkspaceEntity workspace;
        try
        {
            workspace = await workspaceRepository.AddAsync(
                workspaceName, [], WorkspaceType.Basic, WorkspaceVersioningMode.None, WorkspaceCiProvider.None);
        }
        catch (InvalidOperationException ex)
        {
            return RestoreFailure(ex.Message);
        }

        var workspaceId = workspace.WorkspaceId;
        var attach = await LinkAndAttachAsync(workspaceId, repositoryId, progress, cancellationToken, requireEmptyRoot: true);
        if (!attach.Success)
        {
            await DeleteWorkspaceAsync(workspaceId);
            return RestoreFailure(attach.Error ?? "Attaching the Workspace repository failed.");
        }

        // D14 step 3
        progress.Report("Reading Workspace definition...");
        var (_, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);
        var read = await WorkspaceRepositoryFileAccess.ReadAsync(
            workerBridge, args, WorkspaceRepositoryFileAccess.ManifestFilePath, cancellationToken);
        if (read.Error is not null)
            return RestoredWithoutDefinition(workspaceId, read.Error);
        if (!read.Found)
            return RestoredWithoutDefinition(workspaceId, $"{WorkspaceRepositoryFileAccess.ManifestFilePath} was not found in the Workspace repository");
        if (!manifestService.TryParse(read.Content ?? string.Empty, out var manifest, out var parseError) || manifest is null)
            return RestoredWithoutDefinition(workspaceId, parseError ?? "Workspace definition could not be read");

        // D14 step 4
        progress.Report("Applying Workspace profile...");
        await ApplyProfileAsync(workspaceId, manifest.Workspace.Profile, cancellationToken);

        await using var restoreDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // D14 step 5: connectors (D11). Nothing is fabricated for an unresolved one.
        progress.Report("Resolving connectors...");
        var localConnectors = await restoreDb.Connectors
            .AsNoTracking()
            .Where(c => c.ConnectorType == ConnectorType.GitHub)
            .ToListAsync(cancellationToken);
        var unresolvedConnectors = new List<string>();
        foreach (var connector in manifest.Connectors)
        {
            if (ResolveConnector(localConnectors, connector) is null)
                unresolvedConnectors.Add(connector.Url);
        }

        // D14 step 6: repositories by normalized URL among the imported catalog.
        progress.Report("Resolving repositories...");
        var catalog = await restoreDb.Repositories
            .AsNoTracking()
            .Select(r => new { r.RepositoryId, r.CloneUrl })
            .ToListAsync(cancellationToken);
        var catalogByUrl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.OrderBy(e => e.RepositoryId))
            catalogByUrl.TryAdd(RepositoryUrlIdentity.NormalizeRepositoryUrl(entry.CloneUrl), entry.RepositoryId);

        var resolvedIds = new HashSet<int>();
        var unresolvedRepositories = new List<string>();
        foreach (var repository in manifest.Repositories)
        {
            var key = RepositoryUrlIdentity.NormalizeRepositoryUrl(repository.RepositoryUrl);
            if (catalogByUrl.TryGetValue(key, out var resolvedId))
            {
                if (resolvedId != repositoryId)
                    resolvedIds.Add(resolvedId);
            }
            else
            {
                unresolvedRepositories.Add(string.IsNullOrWhiteSpace(repository.ConnectorUrl)
                    ? repository.RepositoryUrl
                    : $"{repository.RepositoryUrl} (connector {repository.ConnectorUrl})");
            }
        }

        foreach (var resolvedId in resolvedIds.OrderBy(id => id))
        {
            restoreDb.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
            {
                WorkspaceId = workspaceId,
                RepositoryId = resolvedId,
                Role = WorkspaceRepositoryRole.Source,
                SyncStatus = RepoSyncStatus.NeedsSync,
            });
        }

        if (resolvedIds.Count > 0)
            await restoreDb.SaveChangesAsync(cancellationToken);

        // D14 step 7: disk and database agree. Step 8 (the sync) is triggered by the caller.
        progress.Report("Writing .gitignore and Workspace definition...");
        var gitIgnore = await manifestService.WriteManagedGitIgnoreAsync(workspaceId, cancellationToken);
        var written = gitIgnore.Success
            ? await manifestService.WriteAuthoritativeManifestAsync(workspaceId, cancellationToken)
            : gitIgnore;
        var warning = written.Success
            ? null
            : $"Workspace restored, but the Workspace definition could not be written: {written.Error}";

        return new RestoreWorkspaceResult(true, workspaceId, warning, unresolvedConnectors, unresolvedRepositories);
    }

    /// <summary>
    /// Adds the Workspace-role link (<c>Role = Workspace</c>) and attaches the repository to the Workspace root
    /// (D4). On any failure the link is removed again and the error is returned.
    /// </summary>
    private async Task<OperationResult> LinkAndAttachAsync(
        int workspaceId,
        int repositoryId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken,
        bool requireEmptyRoot = false)
    {
        int workspaceRepositoryId;
        string cloneUrl;
        string? bearerToken;

        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var repository = await db.Repositories
                .AsNoTracking()
                .Include(r => r.Connector)
                .FirstOrDefaultAsync(r => r.RepositoryId == repositoryId, cancellationToken);
            if (repository is null)
                return OperationResult.Fail("Repository not found.");

            var link = new WorkspaceRepositoryLink
            {
                WorkspaceId = workspaceId,
                RepositoryId = repositoryId,
                Role = WorkspaceRepositoryRole.Workspace,
                SyncStatus = RepoSyncStatus.NeedsSync,
            };
            db.WorkspaceRepositories.Add(link);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                logger.LogWarning(ex, "Could not add the Workspace-role link. WorkspaceId={WorkspaceId}", workspaceId);
                return OperationResult.Fail("This Workspace already has a Workspace repository.");
            }

            workspaceRepositoryId = link.WorkspaceRepositoryId;
            cloneUrl = repository.CloneUrl;
            bearerToken = ConnectorHelpers.UnprotectToken(repository.Connector?.UserToken);
        }

        string? error;
        try
        {
            progress.Report("Attaching Workspace repository...");
            error = await AttachAsync(workspaceId, repositoryId, cloneUrl, bearerToken, requireEmptyRoot, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AttachWorkspaceRepository threw. WorkspaceId={WorkspaceId}", workspaceId);
            error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            await RemoveLinkAsync(workspaceRepositoryId, repositoryId, workspaceId);
            throw;
        }

        if (error is null)
            return OperationResult.Ok();

        await RemoveLinkAsync(workspaceRepositoryId, repositoryId, workspaceId);
        return OperationResult.Fail(error);
    }

    private async Task<string?> AttachAsync(
        int workspaceId,
        int repositoryId,
        string cloneUrl,
        string? bearerToken,
        bool requireEmptyRoot,
        CancellationToken cancellationToken)
    {
        var (_, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);
        var response = await workerBridge.SendCommandAsync(
            WorkerHubMethods.AttachWorkspaceRepository,
            requireEmptyRoot
                ? new
                {
                    workspaceName = args.WorkspaceFolderName,
                    workspaceRoot = args.WorkspaceRoot,
                    workspaceRepositoryName = args.WorkspaceRepositoryName,
                    cloneUrl,
                    bearerToken,
                    workspaceId,
                    repositoryId,
                    requireEmptyRoot = true,
                }
                : (object)new
                {
                    workspaceName = args.WorkspaceFolderName,
                    workspaceRoot = args.WorkspaceRoot,
                    workspaceRepositoryName = args.WorkspaceRepositoryName,
                    cloneUrl,
                    bearerToken,
                    workspaceId,
                    repositoryId,
                },
            cancellationToken);

        if (!response.Success)
            return response.Error ?? "Attaching the Workspace repository failed.";

        var data = WorkerResponseJson.DeserializeWorkerResponse<AttachWorkspaceRepositoryWorkerResponse>(response.Data);
        if (data is null)
            return "The Worker returned an unreadable response for the attach.";
        if (!data.Success)
            return data.ErrorMessage ?? "Attaching the Workspace repository failed.";

        return null;
    }

    private async Task RemoveLinkAsync(int workspaceRepositoryId, int repositoryId, int workspaceId)
    {
        // Rollback must still run when the operation itself was cancelled.
        await using var db = await dbContextFactory.CreateDbContextAsync();
        await WorkspaceRepositoryLinkCleanup.DeleteDependentsAsync(db, [workspaceRepositoryId], [repositoryId], workspaceId);
        await db.WorkspaceRepositories
            .Where(l => l.WorkspaceRepositoryId == workspaceRepositoryId)
            .ExecuteDeleteAsync();
    }

    private async Task DeleteWorkspaceAsync(int workspaceId)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        var links = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId)
            .Select(l => new { l.WorkspaceRepositoryId, l.RepositoryId })
            .ToListAsync();
        await WorkspaceRepositoryLinkCleanup.DeleteDependentsAsync(
            db,
            links.Select(l => l.WorkspaceRepositoryId).ToList(),
            links.Select(l => l.RepositoryId).ToList(),
            workspaceId);
        await db.WorkspaceRepositories.Where(l => l.WorkspaceId == workspaceId).ExecuteDeleteAsync();
        await db.Workspaces.Where(w => w.WorkspaceId == workspaceId).ExecuteDeleteAsync();
    }

    private async Task ApplyProfileAsync(int workspaceId, WorkspaceManifestProfile profile, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var workspace = await db.Workspaces.FirstAsync(w => w.WorkspaceId == workspaceId, cancellationToken);

        // A value this GrayMoon does not know keeps the Basic / None / None default for that axis.
        var type = WorkspaceManifestProfileNames.TryParse(profile.Type, out WorkspaceType parsedType) ? parsedType : workspace.Type;
        var versioning = WorkspaceManifestProfileNames.TryParse(profile.Versioning, out WorkspaceVersioningMode parsedVersioning) ? parsedVersioning : workspace.VersioningMode;
        var ci = WorkspaceManifestProfileNames.TryParse(profile.Ci, out WorkspaceCiProvider parsedCi) ? parsedCi : workspace.CiProvider;

        await WorkspaceProfileTransition.ApplyAsync(db, workspace, type, versioning, ci, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Connector? ResolveConnector(IReadOnlyList<Connector> localConnectors, WorkspaceManifestConnector connector)
    {
        if (!string.Equals(connector.Type, "github", StringComparison.OrdinalIgnoreCase))
            return null;

        var wanted = RepositoryUrlIdentity.NormalizeConnectorUrl(connector.Url);
        return localConnectors
            .Where(c => RepositoryUrlIdentity.NormalizeConnectorUrl(
                RepositoryUrlHelper.GetWebRootFromConnectorApiBase(c.ApiBaseUrl)) == wanted)
            .OrderByDescending(c => c.IsHealthy)
            .ThenBy(c => c.ConnectorId)
            .FirstOrDefault();
    }

    private async Task<(WorkspaceFeatureContextId ContextId, WorkerWorkspaceArgs Args)> GetSpecialContextArgsAsync(
        int workspaceId,
        CancellationToken cancellationToken)
    {
        var contextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        var args = await pathResolver.GetWorkerArgsAsync(contextId, cancellationToken);
        return (contextId, args);
    }

    private static RestoreWorkspaceResult RestoreFailure(string error) =>
        new(false, null, error, [], []);

    private static RestoreWorkspaceResult RestoredWithoutDefinition(int workspaceId, string reason) =>
        new(true, workspaceId, $"Restored without definition: {reason}", [], []);

    private static IProgress<OperationProgress> BindProgress(IWorkspaceLockedOperation op, IProgress<OperationProgress>? progress) =>
        new Progress<OperationProgress>(p =>
        {
            op.ReportProgress(p.Message);
            progress?.Report(p);
        });

    private sealed class AttachWorkspaceRepositoryWorkerResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("success")]
        public bool Success { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("branch")]
        public string? Branch { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("isUnborn")]
        public bool IsUnborn { get; set; }
    }
}
