using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.GitChanges;
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
using GrayMoon.Common.Manifest;
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
public sealed partial class WorkspaceRepositoryOperations(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IWorkerBridge workerBridge,
    IRemoteWorkspaceManifestReader remoteManifestReader,
    IRemoteRepositoryBranchReader branchReader,
    IWorkspaceManifestFile manifestFile,
    IWorkspaceManifestService manifestService,
    IWorkspaceOperationLock operationLock,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceFeatureContextResolver contextResolver,
    WorkspaceRepositoryEntity workspaceRepository,
    WorkspaceService workspaceService,
    IGitChangesWorkspaceScanner gitChangesScanner,
    WorkspaceRepositoryDetacher detacher,
    ILogger<WorkspaceRepositoryOperations> logger,
    GrayMoon.App.Services.Policy.ReferenceImportGuard? referenceImportGuard = null) : IWorkspaceRepositoryOperations, IWorkspaceImportOperations
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
        CancellationToken cancellationToken = default,
        string? setupBranchName = null)
    {
        // The name is checked before the lock is taken: a bad name changes nothing.
        var setupBranch = NormalizeBranch(setupBranchName);
        if (setupBranch is not null && WorkspaceSetupBranch.Validate(setupBranch) is { } invalidBranch)
            return OperationResult.Fail(invalidBranch);

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
                    tcs.TrySetResult(await EnableCoreAsync(workspaceId, repositoryId, setupBranch, report, linked.Token));
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

    /// <summary>The database half of disabling; the caller has already detached the folder (see <see cref="DisableWorkspaceRepositoryAsync"/>).</summary>
    private async Task<OperationResult> DisableLinkRowsAsync(int workspaceId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (await db.WorkspaceFeatures.AnyAsync(f => f.WorkspaceId == workspaceId, cancellationToken))
            return OperationResult.Fail(FeaturesExistOnDisableMessage);

        var link = await db.WorkspaceRepositories
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.WorkspaceId == workspaceId && l.Role == WorkspaceRepositoryRole.Workspace, cancellationToken);
        if (link is null)
            return OperationResult.Fail("This Workspace has no Workspace repository.");

        // Database only. The hooks and watchers were already taken off the folder; its files and .git are never touched.
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

    public async Task<RestoreWorkspacePreflight> PreflightRestoreAsync(int repositoryId, CancellationToken cancellationToken = default, string? branchName = null)
    {
        try
        {
            var (plan, error) = await CheckRemoteDefinitionAsync(repositoryId, NormalizeBranch(branchName), cancellationToken);
            if (plan is null)
                return RestoreWorkspacePreflight.Failed(error!);

            return new RestoreWorkspacePreflight
            {
                Success = true,
                WorkspaceType = plan.Plan.Type,
                VersioningMode = plan.Plan.Versioning,
                CiProvider = plan.Plan.Ci,
                RepositoryCount = plan.Plan.RepositoryCount,
                ConnectorCount = plan.Plan.ConnectorCount,
                MissingConnectors = plan.Plan.MissingConnectors,
                MissingRepositories = plan.Plan.MissingRepositories,
                ReferenceAggregate = WorkspaceReferenceAggregates.Of(plan.Plan.RepositoryCount, plan.Plan.ReferenceRepositoryCount),
                ReferenceDefault = plan.Plan.ReferenceDefault,
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
        CancellationToken cancellationToken = default,
        string? branchName = null)
    {
        try
        {
            return await RestoreCoreAsync(repositoryId, workspaceName, NormalizeBranch(branchName), progress, cancellationToken);
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
        string? setupBranch,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        // (a) the Worker must be connected and version-matched before anything changes
        if (workerBridge.GetUnavailableReason() is { } workerUnavailable)
            return OperationResult.Fail(workerUnavailable);

        int? sourceLinkId;
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

            // (d) A Source repository of this Workspace is moved: its link changes role instead of being refused.
            sourceLinkId = await db.WorkspaceRepositories
                .Where(l => l.WorkspaceId == workspaceId && l.RepositoryId == repositoryId)
                .Select(l => (int?)l.WorkspaceRepositoryId)
                .FirstOrDefaultAsync(cancellationToken);

            if (!await db.Repositories.AnyAsync(r => r.RepositoryId == repositoryId, cancellationToken))
                return OperationResult.Fail("Repository not found.");
        }

        // The setup branch must be new. The remote is asked before anything is linked or cloned, so a name that
        // is taken changes nothing. A fresh clone has no other local branches, so the remote list is the whole answer.
        if (setupBranch is not null)
        {
            progress.Report("Checking branches...");
            var remote = await branchReader.ListAsync(repositoryId, cancellationToken);
            if (remote.Error is not null)
                return OperationResult.Fail($"Could not check whether branch '{setupBranch}' already exists: {remote.Error}");
            if (remote.Branches.Any(b => string.Equals(b, setupBranch, StringComparison.OrdinalIgnoreCase)))
                return OperationResult.Fail(WorkspaceSetupBranch.ExistsMessage(setupBranch));
        }

        // (e) + (f), with rollback of the link on failure
        progress.Report("Linking Workspace repository...");
        var attach = await LinkAndAttachAsync(workspaceId, repositoryId, progress, cancellationToken, sourceLinkId: sourceLinkId);
        if (!attach.Success)
            return OperationResult.Fail(attach.Error ?? "Attaching the Workspace repository failed.");

        // A folder that is already this repository keeps its graymoon.json. Rewriting it from the database
        // would drop version files and repositories that have not been imported yet.
        if (!attach.AdoptedExisting)
        {
            // A repository with no commits has no default branch to protect, so it gets no setup branch.
            if (setupBranch is not null && !attach.IsUnborn)
            {
                progress.Report($"Creating branch {setupBranch}...");
                var branchError = await CreateSetupBranchAsync(workspaceId, repositoryId, attach, setupBranch, cancellationToken);
                if (branchError is not null)
                {
                    return OperationResult.Fail(
                        $"The Workspace repository was attached, but branch '{setupBranch}' could not be created, so graymoon.json and .gitignore were not written: {branchError}");
                }
            }

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
        }
        else
        {
            logger.LogInformation(
                "Workspace repository already occupied the folder. graymoon.json was left unchanged. WorkspaceId={WorkspaceId} RepositoryId={RepositoryId}",
                workspaceId, repositoryId);
        }

        // Clone plus the .gitignore and definition writes leave uncommitted files. Scan now so the
        // nav dot and Changes data update without opening the Changes page.
        var contextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        await RefreshGitChangesAfterEnableAsync(workspaceId, contextId, repositoryId, cancellationToken);

        // (h)
        var notes = new List<string>();
        if (attach.KeptFileCount > 0)
            notes.Add(KeptFilesMessage(attach.KeptFiles, attach.KeptFileCount));
        if (sourceLinkId is not null)
            notes.Add(MovedFromSourceMessage(attach.RepositoryName ?? "The repository"));
        if (attach.MovedSourceWarning is { } movedSourceWarning)
            notes.Add(movedSourceWarning);
        return notes.Count == 0
            ? OperationResult.Ok()
            : OperationResult.Ok() with { Warnings = notes, WarningSummary = string.Join(" ", notes) };
    }

    private const string CheckedOutIntoFolder = "The repository's files were checked out into the Workspace folder, which is now its working copy.";

    /// <summary>Files already in the Workspace folder that differ from the repository were kept, not overwritten.</summary>
    public static string KeptFilesMessage(IReadOnlyList<string> files, int count)
    {
        var names = string.Join(", ", files);
        var more = count > files.Count ? $" and {count - files.Count} more" : "";
        return count == 1
            ? $"{CheckedOutIntoFolder} 1 file already in the Workspace folder was kept instead of the repository's version: {names}. It shows in Changes."
            : $"{CheckedOutIntoFolder} {count} files already in the Workspace folder were kept instead of the repository's versions: {names}{more}. They show in Changes.";
    }

    /// <summary>A Source repository became the Workspace repository; its earlier checkout is left on disk.</summary>
    public static string MovedFromSourceMessage(string repositoryName) =>
        $"{repositoryName} is no longer a Source repository of this Workspace. Its folder '{repositoryName}' was left on disk and is no longer managed by GrayMoon.";

    /// <summary>
    /// Creates <paramref name="setupBranch"/> from the branch the attach checked out and checks it out, so the files
    /// written next are not on the default branch. Returns the error, or null when HEAD is on the new branch.
    /// </summary>
    private async Task<string?> CreateSetupBranchAsync(
        int workspaceId,
        int repositoryId,
        LinkAttachResult attach,
        string setupBranch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(attach.Branch) || string.IsNullOrWhiteSpace(attach.RepositoryName))
            return "The Workspace repository is not on a branch.";

        var (_, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);
        var response = await workerBridge.SendCommandAsync(
            "CreateBranch",
            new
            {
                workspaceName = args.WorkspaceFolderName,
                repositoryName = attach.RepositoryName,
                newBranchName = setupBranch,
                baseBranchName = attach.Branch,
                workspaceRoot = args.WorkspaceRoot,
                workspaceRepositoryName = args.WorkspaceRepositoryName ?? attach.RepositoryName,
                workspaceId,
                repositoryId,
            },
            cancellationToken);

        if (!response.Success)
            return response.Error ?? "Creating the branch failed.";

        var data = WorkerResponseJson.DeserializeWorkerResponse<CreateBranchWorkerResponse>(response.Data);
        if (data is null)
            return "The Worker returned an unreadable response for the new branch.";
        if (!data.Success)
            return data.ErrorMessage ?? "Creating the branch failed.";

        return string.Equals(data.CurrentBranch, setupBranch, StringComparison.Ordinal)
            ? null
            : $"The repository is on '{data.CurrentBranch ?? "no branch"}' instead.";
    }

    private async Task RefreshGitChangesAfterEnableAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        CancellationToken cancellationToken)
    {
        try
        {
            await gitChangesScanner.ScanWorkspaceAsync(
                workspaceId,
                contextId,
                cancellationToken,
                repositoryId: repositoryId,
                persistImmediately: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Git Changes refresh after enabling the Workspace repository was cancelled. WorkspaceId={WorkspaceId}", workspaceId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Git Changes refresh after enabling the Workspace repository failed. WorkspaceId={WorkspaceId}", workspaceId);
        }
    }

    private async Task<RestoreWorkspaceResult> RestoreCoreAsync(
        int repositoryId,
        string workspaceName,
        string? branchName,
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
        var (checkedDefinition, definitionError) = await CheckRemoteDefinitionAsync(repositoryId, NormalizeBranch(branchName), cancellationToken);
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
            var (result, error) = await RestoreIntoWorkspaceAsync(workspaceId, repositoryId, branchName, checkedDefinition.Context, progress, cancellationToken);
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
        string? branchName,
        RestoreContext context,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken,
        bool adoptExisting = false)
    {
        // Import from disk adopts the repository that is already in the folder; Restore clones into an empty one.
        var done = adoptExisting ? "Workspace imported" : "Workspace restored";
        var attach = await LinkAndAttachAsync(
            workspaceId, repositoryId, progress, cancellationToken,
            requireEmptyRoot: !adoptExisting,
            attachMessage: adoptExisting ? "Adopting Workspace repository..." : "Cloning Workspace repository...",
            branchName: branchName);
        if (!attach.Success)
            return (null, attach.Error ?? (adoptExisting ? "Adopting the Workspace repository failed." : "Cloning the Workspace repository failed."));
        if (adoptExisting && !attach.AdoptedExisting)
            return (null, "The folder is no longer the Workspace repository it was when it was checked.");

        // D14 step 3: the remote branch may have changed since the preflight, so the local copy is validated again.
        var (_, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);
        var read = await WorkspaceRepositoryFileAccess.ReadAsync(
            workerBridge, args, manifestFile.FileName, cancellationToken);
        if (read.Error is not null)
            return (null, $"The Workspace definition could not be read after cloning: {read.Error}");

        var localContent = read.Found ? read.Content ?? string.Empty : null;
        var (plan, planError) = RestoreDefinitionEvaluator.Evaluate(manifestFile, localContent, context.CloneUrl, context.Connectors, context.Catalog);
        if (plan is null)
            return (null, planError);

        // D14 step 4
        progress.Report("Applying Workspace profile...");
        await ApplyProfileAsync(workspaceId, plan.Type, plan.Versioning, plan.Ci, cancellationToken);
        await ApplyReferenceDefaultAsync(workspaceId, plan.ReferenceDefault, cancellationToken);

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
                    // Set before the caller's first Sync, so a read-only repository is never cloned as a writable one.
                    IsReference = plan.ReferenceBySourceRepositoryId.GetValueOrDefault(resolvedId),
                });
            }

            await linkDb.SaveChangesAsync(cancellationToken);
        }

        // The definition is the source of truth for version files; entries of repositories that are not here are skipped.
        var skippedVersionFiles = await manifestService.MergeVersionFilesIntoDatabaseAsync(
            workspaceId, plan.Manifest.VersionFiles, cancellationToken);

        // D14 step 7. Step 8 (the Sync) is requested by the caller.
        progress.Report("Preparing Workspace files...");
        string? warning = null;
        var gitIgnore = await manifestService.WriteManagedGitIgnoreAsync(workspaceId, cancellationToken);
        if (!gitIgnore.Success)
        {
            warning = $"{done}, but .gitignore could not be updated: {gitIgnore.Error}";
        }
        else if (skippedVersionFiles.Count == 0
                 && await IsDefinitionRewriteRequiredAsync(workspaceId, plan, localContent!, cancellationToken))
        {
            var written = await manifestService.WriteAuthoritativeManifestAsync(workspaceId, cancellationToken);
            if (!written.Success)
                warning = $"{done}, but the Workspace definition could not be written: {written.Error}";
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
    /// with repositories, connectors or version files that are missing here is never rewritten: that would drop them from the file.
    /// A leftover <c>workspace.name</c> is not a reason to rewrite, so restoring under another local name does not dirty the repository.
    /// </summary>
    private async Task<bool> IsDefinitionRewriteRequiredAsync(
        int workspaceId,
        RestoreDefinitionPlan plan,
        string fileContent,
        CancellationToken cancellationToken)
    {
        if (plan.MissingRepositories.Count > 0 || plan.MissingConnectors.Count > 0)
            return false;

        if (!manifestFile.TryParse(fileContent, out var fileManifest, out _) || fileManifest is null)
            return true;

        var database = WorkspaceRepositoryRevisionPins.CopyOnto(
            WorkspaceManifestSchemaPolicy.KeepSchema(
                await manifestService.BuildFromDatabaseAsync(workspaceId, cancellationToken),
                fileManifest),
            fileManifest) with
        {
            CodeGraph = fileManifest.CodeGraph,
            Ai = fileManifest.Ai,
        };
        // Parsing drops workspace.name, so both sides omit it. A leftover name does not differ.
        return !string.Equals(
            manifestFile.Serialize(database),
            manifestFile.Serialize(fileManifest),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes a Workspace whose restore failed: the root folder first (the Worker deletes it only when it can prove
    /// the restore created it), then the database row and links. Never throws; returns what was left behind, if any.
    /// </summary>
    private async Task<string?> RollbackRestoreAsync(int workspaceId, string cloneUrl, bool folderExisted)
    {
        var residue = new List<string>();

        // The attach wrote hooks into the root. They go first, while the links still say which folders were managed;
        // when the folder is deleted below they go with it anyway.
        try
        {
            var detached = await detacher.DetachAsync(workspaceId, null, workspaceRemoved: true, cancellationToken: CancellationToken.None);
            if (!detached.Success)
                residue.Add(DetachResidue(detached));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Restore rollback could not remove the Git hooks. WorkspaceId={WorkspaceId}", workspaceId);
            residue.Add($"The Git hooks GrayMoon installed could not be removed: {ex.Message}");
        }

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
    private async Task<(CheckedDefinition? Definition, string? Error)> CheckRemoteDefinitionAsync(int repositoryId, string? branchName, CancellationToken cancellationToken)
    {
        var context = await LoadRestoreContextAsync(repositoryId, cancellationToken);
        if (context is null)
            return (null, "Repository not found.");

        var read = await remoteManifestReader.ReadAsync(repositoryId, branchName, cancellationToken);
        if (read.Error is not null)
            return (null, read.Error);

        var (plan, error) = RestoreDefinitionEvaluator.Evaluate(
            manifestFile, read.Found ? read.Content ?? string.Empty : null, context.CloneUrl, context.Connectors, context.Catalog);
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
            .Where(c => c.ConnectorType == ConnectorType.GitHub && c.RemovedAtUtc == null)
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
    private async Task<LinkAttachResult> LinkAndAttachAsync(
        int workspaceId,
        int repositoryId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken,
        bool requireEmptyRoot = false,
        string attachMessage = "Attaching Workspace repository...",
        string? branchName = null,
        int? sourceLinkId = null)
    {
        int workspaceRepositoryId;
        string cloneUrl;
        string repositoryName;
        string? bearerToken;

        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var repository = await db.Repositories
                .AsNoTracking()
                .Include(r => r.Connector)
                .FirstOrDefaultAsync(r => r.RepositoryId == repositoryId, cancellationToken);
            if (repository is null)
                return LinkAttachResult.Failed("Repository not found.");

            try
            {
                if (sourceLinkId is int existingLinkId)
                {
                    // A Source repository is moved by changing its link's role. Nothing is deleted until the attach
                    // has succeeded, so a failed attach puts the role back and the Source keeps all its state.
                    await db.WorkspaceRepositories
                        .Where(l => l.WorkspaceRepositoryId == existingLinkId)
                        .ExecuteUpdateAsync(s => s.SetProperty(l => l.Role, WorkspaceRepositoryRole.Workspace), cancellationToken);
                    workspaceRepositoryId = existingLinkId;
                }
                else
                {
                    var link = new WorkspaceRepositoryLink
                    {
                        WorkspaceId = workspaceId,
                        RepositoryId = repositoryId,
                        Role = WorkspaceRepositoryRole.Workspace,
                        SyncStatus = RepoSyncStatus.NeedsSync,
                    };
                    db.WorkspaceRepositories.Add(link);
                    await db.SaveChangesAsync(cancellationToken);
                    workspaceRepositoryId = link.WorkspaceRepositoryId;
                }
            }
            catch (DbUpdateException ex)
            {
                logger.LogWarning(ex, "Could not add the Workspace-role link. WorkspaceId={WorkspaceId}", workspaceId);
                return LinkAttachResult.Failed("This Workspace already has a Workspace repository.");
            }

            cloneUrl = repository.CloneUrl;
            repositoryName = repository.RepositoryName;
            bearerToken = ConnectorHelpers.UnprotectToken(repository.Connector?.UserToken);
        }

        string? error;
        AttachWorkspaceRepositoryWorkerResponse? attached = null;
        try
        {
            progress.Report(attachMessage);
            (error, attached) = await AttachAsync(workspaceId, repositoryId, cloneUrl, bearerToken, requireEmptyRoot, branchName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AttachWorkspaceRepository threw. WorkspaceId={WorkspaceId}", workspaceId);
            error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            await UndoLinkAsync(workspaceRepositoryId, repositoryId, workspaceId, sourceLinkId);
            throw;
        }

        string? movedSourceWarning = null;
        if (error is null && attached is not null)
        {
            if (sourceLinkId is not null)
                movedSourceWarning = await DetachAndReplaceMovedSourceAsync(workspaceRepositoryId, repositoryId, repositoryName, workspaceId);

            return new LinkAttachResult(true, null, attached.AdoptedExisting, repositoryName, attached.Branch, attached.IsUnborn)
            {
                KeptFiles = attached.KeptFiles ?? [],
                KeptFileCount = attached.KeptFileCount,
                MovedSourceWarning = movedSourceWarning,
            };
        }

        await UndoLinkAsync(workspaceRepositoryId, repositoryId, workspaceId, sourceLinkId);
        return LinkAttachResult.Failed(error ?? "Attaching the Workspace repository failed.");
    }

    /// <summary>A link this operation added is removed; a Source link it changed gets its role back.</summary>
    private async Task UndoLinkAsync(int workspaceRepositoryId, int repositoryId, int workspaceId, int? sourceLinkId)
    {
        if (sourceLinkId is null)
        {
            await RemoveLinkAsync(workspaceRepositoryId, repositoryId, workspaceId);
            return;
        }

        // Must still run when the operation itself was cancelled.
        await using var db = await dbContextFactory.CreateDbContextAsync();
        await db.WorkspaceRepositories
            .Where(l => l.WorkspaceRepositoryId == workspaceRepositoryId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Role, WorkspaceRepositoryRole.Source));
    }

    /// <summary>
    /// The moved Source link still carries the state of its own checkout (branch, projects, changes, read only).
    /// Now that the repository is attached to the root, that state is removed and the link starts as a new
    /// Workspace-role link, exactly like one that was never a Source.
    /// </summary>
    private async Task ReplaceMovedSourceLinkAsync(int workspaceRepositoryId, int repositoryId, int workspaceId)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await WorkspaceRepositoryLinkCleanup.DeleteDependentsAsync(db, [workspaceRepositoryId], [repositoryId], workspaceId);
        await db.WorkspaceRepositories
            .Where(l => l.WorkspaceRepositoryId == workspaceRepositoryId)
            .ExecuteDeleteAsync();
        db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
        {
            WorkspaceId = workspaceId,
            RepositoryId = repositoryId,
            Role = WorkspaceRepositoryRole.Workspace,
            SyncStatus = RepoSyncStatus.NeedsSync,
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    /// <summary><paramref name="Branch"/> is the branch the attach left checked out; <paramref name="IsUnborn"/> means the repository has no commits.</summary>
    private sealed record LinkAttachResult(
        bool Success,
        string? Error,
        bool AdoptedExisting,
        string? RepositoryName = null,
        string? Branch = null,
        bool IsUnborn = false)
    {
        /// <summary>Files already in the folder that were kept instead of the repository's version (first few names).</summary>
        public IReadOnlyList<string> KeptFiles { get; init; } = [];
        public int KeptFileCount { get; init; }

        /// <summary>Set when the old folder of a moved Source repository still carries GrayMoon's hooks.</summary>
        public string? MovedSourceWarning { get; init; }

        public static LinkAttachResult Failed(string error) => new(false, error, false);
    }

    private async Task<(string? Error, AttachWorkspaceRepositoryWorkerResponse? Attached)> AttachAsync(
        int workspaceId,
        int repositoryId,
        string cloneUrl,
        string? bearerToken,
        bool requireEmptyRoot,
        string? branchName,
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
                branchName,
            },
            cancellationToken);

        if (!response.Success)
            return (response.Error ?? "Attaching the Workspace repository failed.", null);

        var data = WorkerResponseJson.DeserializeWorkerResponse<AttachWorkspaceRepositoryWorkerResponse>(response.Data);
        if (data is null)
            return ("The Worker returned an unreadable response for the attach.", null);
        if (!data.Success)
            return (data.ErrorMessage ?? "Attaching the Workspace repository failed.", null);

        return (null, data);
    }

    /// <summary>A blank branch means "the default branch" (null). The name is otherwise kept exactly as the provider spelled it.</summary>
    private static string? NormalizeBranch(string? branchName) =>
        string.IsNullOrWhiteSpace(branchName) ? null : branchName.Trim();

    private async Task RemoveLinkAsync(int workspaceRepositoryId, int repositoryId, int workspaceId)
    {
        // Normally the attach failed before it wrote hooks; this covers an attach that finished on the Worker while
        // its answer was lost. The link goes either way: it was only just added and holds no state.
        await DetachQuietlyAsync(workspaceId, [workspaceRepositoryId]);

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

    /// <summary>The read-only default for repositories added later. It changes no existing link.</summary>
    private async Task ApplyReferenceDefaultAsync(int workspaceId, bool referenceDefault, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var workspace = await db.Workspaces.FirstAsync(w => w.WorkspaceId == workspaceId, cancellationToken);
        if (workspace.ReferenceDefault == referenceDefault)
            return;

        workspace.ReferenceDefault = referenceDefault;
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

        [System.Text.Json.Serialization.JsonPropertyName("adoptedExisting")]
        public bool AdoptedExisting { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("keptFiles")]
        public List<string>? KeptFiles { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("keptFileCount")]
        public int KeptFileCount { get; set; }
    }

    private sealed class CreateBranchWorkerResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("success")]
        public bool Success { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("currentBranch")]
        public string? CurrentBranch { get; set; }
    }

    private sealed class DiscardWorkspaceRootWorkerResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("removed")]
        public bool Removed { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}
