using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.GitHub;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.WorkspaceManifest;
using GrayMoon.App.Services.Workspaces;
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
/// structural lock; every database change goes through factory-created contexts. Worker compatibility is not a
/// per-feature question: the App/Worker version lock (<see cref="WorkerVersionPolicy"/>) decides whether the Worker
/// may run commands at all.
/// </summary>
public sealed class WorkspaceRepositoryOperations(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IWorkerBridge workerBridge,
    IRemoteWorkspaceManifestReader remoteManifestReader,
    IWorkspaceManifestService manifestService,
    IWorkspaceOperationLock operationLock,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceFeatureContextResolver contextResolver,
    WorkspaceRepositoryEntity workspaceRepository,
    WorkspaceService workspaceService,
    ILogger<WorkspaceRepositoryOperations> logger) : IWorkspaceRepositoryOperations
{
    internal const string NonEmptyFolderMessage =
        "This folder already contains files. Choose another Workspace name or move the existing files.";

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

    public async Task<RestoreWorkspacePreflight> PreflightRestoreAsync(int repositoryId, CancellationToken cancellationToken = default)
    {
        try
        {
            var (plan, error) = await CheckRemoteDefinitionAsync(repositoryId, cancellationToken);
            if (plan is null)
                return RestoreWorkspacePreflight.Failed(error!);

            return new RestoreWorkspacePreflight
            {
                Success = true,
                DefinitionName = plan.Plan.Manifest.Workspace.Name,
                WorkspaceType = plan.Plan.Type,
                VersioningMode = plan.Plan.Versioning,
                CiProvider = plan.Plan.Ci,
                RepositoryCount = plan.Plan.RepositoryCount,
                ConnectorCount = plan.Plan.ConnectorCount,
                MissingConnectors = plan.Plan.MissingConnectors,
                MissingRepositories = plan.Plan.MissingRepositories,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Restore preflight threw. RepositoryId={RepositoryId}", repositoryId);
            return RestoreWorkspacePreflight.Failed(ex.Message);
        }
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
            return RestoreWorkspaceResult.Failed(ex.Message);
        }
    }

    private async Task<OperationResult> EnableCoreAsync(
        int workspaceId,
        int repositoryId,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        // (a) the Worker must be connected and version-matched before anything changes
        if (workerBridge.GetUnavailableReason() is { } workerUnavailable)
            return OperationResult.Fail(workerUnavailable);

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
        // Everything below until the Workspace row is created only reads: a failure here changes nothing.
        var name = workspaceName?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return RestoreWorkspaceResult.Failed("Workspace name is required.");

        if (workerBridge.GetUnavailableReason() is { } workerUnavailable)
            return RestoreWorkspaceResult.Failed(workerUnavailable);

        progress.Report("Checking Workspace definition...");
        var (checkedDefinition, definitionError) = await CheckRemoteDefinitionAsync(repositoryId, cancellationToken);
        if (checkedDefinition is null)
            return RestoreWorkspaceResult.Failed(definitionError!);

        var folder = await workspaceService.GetDirectoryStateAsync(name, null, cancellationToken);
        if (folder.Error is not null)
            return RestoreWorkspaceResult.Failed($"Could not check the Workspace folder: {folder.Error}");
        if (folder.Exists && folder.IsEmpty != true)
            return RestoreWorkspaceResult.Failed(NonEmptyFolderMessage);

        // D14 step 2: Workspace row (Basic / None / None); from here on a failure rolls the Workspace back.
        progress.Report("Preparing Workspace...");
        WorkspaceEntity workspace;
        try
        {
            workspace = await workspaceRepository.AddAsync(
                name, [], WorkspaceType.Basic, WorkspaceVersioningMode.None, WorkspaceCiProvider.None);
        }
        catch (InvalidOperationException ex)
        {
            return RestoreWorkspaceResult.Failed(ex.Message);
        }

        var workspaceId = workspace.WorkspaceId;
        var cloneUrl = checkedDefinition.Context.CloneUrl;
        try
        {
            var (result, error) = await RestoreIntoWorkspaceAsync(workspaceId, repositoryId, checkedDefinition.Context, progress, cancellationToken);
            if (result is not null)
                return result;

            var residue = await RollbackRestoreAsync(workspaceId, cloneUrl, folder.Exists);
            return RestoreWorkspaceResult.Failed(error!, residue);
        }
        catch (OperationCanceledException)
        {
            await RollbackRestoreAsync(workspaceId, cloneUrl, folder.Exists);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Restore failed after the Workspace was created. WorkspaceId={WorkspaceId}", workspaceId);
            var residue = await RollbackRestoreAsync(workspaceId, cloneUrl, folder.Exists);
            return RestoreWorkspaceResult.Failed(ex.Message, residue);
        }
    }

    /// <summary>
    /// D14 steps 2-7 for a Workspace row that already exists. Returns the result on success, or the error that must
    /// roll the Workspace back. Writing .gitignore and the definition happens after the Workspace is usable, so those
    /// failures are warnings: the drift banner offers "Write Workspace definition to disk" to repair them.
    /// </summary>
    private async Task<(RestoreWorkspaceResult? Result, string? Error)> RestoreIntoWorkspaceAsync(
        int workspaceId,
        int repositoryId,
        RestoreContext context,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var attach = await LinkAndAttachAsync(
            workspaceId, repositoryId, progress, cancellationToken, requireEmptyRoot: true, attachMessage: "Cloning Workspace repository...");
        if (!attach.Success)
            return (null, attach.Error ?? "Cloning the Workspace repository failed.");

        // D14 step 3: the remote branch may have changed since the preflight, so the local copy is validated again.
        var (_, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);
        var read = await WorkspaceRepositoryFileAccess.ReadAsync(
            workerBridge, args, WorkspaceRepositoryFileAccess.ManifestFilePath, cancellationToken);
        if (read.Error is not null)
            return (null, $"The Workspace definition could not be read after cloning: {read.Error}");

        var localContent = read.Found ? read.Content ?? string.Empty : null;
        var (plan, planError) = RestoreDefinitionEvaluator.Evaluate(localContent, context.CloneUrl, context.Connectors, context.Catalog);
        if (plan is null)
            return (null, planError);

        // D14 step 4
        progress.Report("Applying Workspace profile...");
        await ApplyProfileAsync(workspaceId, plan.Type, plan.Versioning, plan.Ci, cancellationToken);

        // D14 steps 5-6: every Source repository that resolves locally; the rest is reported, never fabricated.
        progress.Report(plan.SourceRepositoryIds.Count > 0
            ? $"Linking {plan.SourceRepositoryIds.Count} repositories..."
            : "Linking repositories...");
        if (plan.SourceRepositoryIds.Count > 0)
        {
            await using var linkDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            foreach (var resolvedId in plan.SourceRepositoryIds)
            {
                linkDb.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
                {
                    WorkspaceId = workspaceId,
                    RepositoryId = resolvedId,
                    Role = WorkspaceRepositoryRole.Source,
                    SyncStatus = RepoSyncStatus.NeedsSync,
                });
            }

            await linkDb.SaveChangesAsync(cancellationToken);
        }

        // D14 step 7. Step 8 (the Sync) is requested by the caller.
        progress.Report("Preparing Workspace files...");
        string? warning = null;
        var gitIgnore = await manifestService.WriteManagedGitIgnoreAsync(workspaceId, cancellationToken);
        if (!gitIgnore.Success)
        {
            warning = $"Workspace restored, but .gitignore could not be updated: {gitIgnore.Error}";
        }
        else if (await IsDefinitionRewriteRequiredAsync(workspaceId, plan, localContent!, cancellationToken))
        {
            var written = await manifestService.WriteAuthoritativeManifestAsync(workspaceId, cancellationToken);
            if (!written.Success)
                warning = $"Workspace restored, but the Workspace definition could not be written: {written.Error}";
        }

        return (new RestoreWorkspaceResult
        {
            Success = true,
            WorkspaceId = workspaceId,
            Warning = warning,
            UnresolvedConnectors = plan.MissingConnectors,
            UnresolvedRepositories = plan.MissingRepositories,
        }, null);
    }

    /// <summary>
    /// The restored definition is rewritten only when it is complete on this computer and its canonical form differs
    /// (for example duplicate entries, or the Workspace repository listed as its own Source repository). A definition
    /// with repositories or connectors that are missing here is never rewritten: that would drop them from the file.
    /// The Workspace name in the file is not a reason to rewrite, so choosing a local name does not dirty the repository.
    /// </summary>
    private async Task<bool> IsDefinitionRewriteRequiredAsync(
        int workspaceId,
        RestoreDefinitionPlan plan,
        string fileContent,
        CancellationToken cancellationToken)
    {
        if (plan.MissingRepositories.Count > 0 || plan.MissingConnectors.Count > 0)
            return false;

        var database = await manifestService.BuildFromDatabaseAsync(workspaceId, cancellationToken);
        var canonical = manifestService.Serialize(database with
        {
            Workspace = database.Workspace with { Name = plan.Manifest.Workspace.Name },
        });
        return !string.Equals(canonical, fileContent.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes a Workspace whose restore failed: the root folder first (the Worker deletes it only when it can prove
    /// the restore created it), then the database row and links. Never throws; returns what was left behind, if any.
    /// </summary>
    private async Task<string?> RollbackRestoreAsync(int workspaceId, string cloneUrl, bool folderExisted)
    {
        var residue = new List<string>();
        try
        {
            var (_, args) = await GetSpecialContextArgsAsync(workspaceId, CancellationToken.None);
            var folderPath = Path.Combine(args.WorkspaceRoot ?? string.Empty, args.WorkspaceFolderName ?? string.Empty);
            var response = await workerBridge.SendCommandAsync(
                WorkerHubMethods.DiscardWorkspaceRoot,
                new
                {
                    workspaceName = args.WorkspaceFolderName,
                    workspaceRoot = args.WorkspaceRoot,
                    cloneUrl,
                    keepFolder = folderExisted,
                },
                CancellationToken.None);
            var data = response.Success
                ? WorkerResponseJson.DeserializeWorkerResponse<DiscardWorkspaceRootWorkerResponse>(response.Data)
                : null;
            if (!response.Success)
                residue.Add($"The Workspace folder {folderPath} could not be cleaned up: {response.Error}");
            else if (data is null)
                residue.Add($"The Workspace folder {folderPath} could not be cleaned up: the Worker returned an unreadable response.");
            else if (!data.Removed)
                residue.Add($"The Workspace folder {folderPath} was left in place: {data.Reason}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Restore rollback could not clean the Workspace folder. WorkspaceId={WorkspaceId}", workspaceId);
            residue.Add($"The Workspace folder could not be cleaned up: {ex.Message}");
        }

        try
        {
            await DeleteWorkspaceAsync(workspaceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore rollback could not delete the Workspace. WorkspaceId={WorkspaceId}", workspaceId);
            residue.Add($"The Workspace could not be removed from GrayMoon: {ex.Message}");
        }

        logger.LogInformation(
            "Restore rolled back. WorkspaceId={WorkspaceId} ResidueCount={ResidueCount}", workspaceId, residue.Count);
        return residue.Count == 0 ? null : string.Join(" ", residue);
    }

    /// <summary>Reads the repository's definition through its connector and evaluates it against this computer.</summary>
    private async Task<(CheckedDefinition? Definition, string? Error)> CheckRemoteDefinitionAsync(int repositoryId, CancellationToken cancellationToken)
    {
        var context = await LoadRestoreContextAsync(repositoryId, cancellationToken);
        if (context is null)
            return (null, "Repository not found.");

        var read = await remoteManifestReader.ReadAsync(repositoryId, cancellationToken);
        if (read.Error is not null)
            return (null, read.Error);

        var (plan, error) = RestoreDefinitionEvaluator.Evaluate(
            read.Found ? read.Content ?? string.Empty : null, context.CloneUrl, context.Connectors, context.Catalog);
        return plan is null ? (null, error) : (new CheckedDefinition(context, plan), null);
    }

    private async Task<RestoreContext?> LoadRestoreContextAsync(int repositoryId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var cloneUrl = await db.Repositories
            .AsNoTracking()
            .Where(r => r.RepositoryId == repositoryId)
            .Select(r => r.CloneUrl)
            .FirstOrDefaultAsync(cancellationToken);
        if (cloneUrl is null)
            return null;

        var connectors = await db.Connectors
            .AsNoTracking()
            .Where(c => c.ConnectorType == ConnectorType.GitHub)
            .ToListAsync(cancellationToken);
        var catalog = await db.Repositories
            .AsNoTracking()
            .Select(r => new RestoreCatalogEntry(r.RepositoryId, r.CloneUrl))
            .ToListAsync(cancellationToken);
        return new RestoreContext(cloneUrl, connectors, catalog);
    }

    private sealed record RestoreContext(string CloneUrl, IReadOnlyList<Connector> Connectors, IReadOnlyList<RestoreCatalogEntry> Catalog);

    private sealed record CheckedDefinition(RestoreContext Context, RestoreDefinitionPlan Plan);

    /// <summary>
    /// Adds the Workspace-role link (<c>Role = Workspace</c>) and attaches the repository to the Workspace root
    /// (D4). On any failure the link is removed again and the error is returned.
    /// </summary>
    private async Task<OperationResult> LinkAndAttachAsync(
        int workspaceId,
        int repositoryId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken,
        bool requireEmptyRoot = false,
        string attachMessage = "Attaching Workspace repository...")
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
            progress.Report(attachMessage);
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
            new
            {
                workspaceName = args.WorkspaceFolderName,
                workspaceRoot = args.WorkspaceRoot,
                workspaceRepositoryName = args.WorkspaceRepositoryName,
                cloneUrl,
                bearerToken,
                workspaceId,
                repositoryId,
                requireEmptyRoot,
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

    /// <summary>The profile was validated by <see cref="RestoreDefinitionEvaluator"/>; unknown values never get here.</summary>
    private async Task ApplyProfileAsync(
        int workspaceId,
        WorkspaceType type,
        WorkspaceVersioningMode versioning,
        WorkspaceCiProvider ci,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var workspace = await db.Workspaces.FirstAsync(w => w.WorkspaceId == workspaceId, cancellationToken);
        await WorkspaceProfileTransition.ApplyAsync(db, workspace, type, versioning, ci, cancellationToken);
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

    private sealed class DiscardWorkspaceRootWorkerResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("removed")]
        public bool Removed { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}
