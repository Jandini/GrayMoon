using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>How a write path wants <see cref="WorkspaceRepositoryLink.SyncStatus"/> handled.</summary>
public enum SyncStatusWrite
{
    /// <summary>Leave the persisted status alone (branch-only operations such as delete-branch or update-from-default).</summary>
    Leave,

    /// <summary>
    /// Derive it from the snapshot: Error without a branch or tag, NeedsSync without a known default branch (unless
    /// the remote is empty or a hook keeps an earlier InSync), otherwise InSync. The version is deliberately not
    /// considered, so a workspace without repository versioning syncs to InSync like any other.
    /// </summary>
    Derive,

    /// <summary>Force InSync.</summary>
    InSync,

    /// <summary>Force Error.</summary>
    Error,
}

/// <summary>Per-call knobs that are about the App's own bookkeeping rather than about git state.</summary>
public sealed class RepositoryStateWriteOptions
{
    public SyncStatusWrite SyncStatus { get; init; } = SyncStatusWrite.Leave;

    /// <summary>
    /// When the snapshot carries an error the row still counts as InSync, so the grid shows the error
    /// badge rather than a "retry" chip. Only the hook flow wants this.
    /// </summary>
    public bool ErrorMessageForcesInSync { get; init; }

    /// <summary>Reconcile the persisted pull request against the branch that is now checked out.</summary>
    public bool ReconcilePullRequest { get; init; }
}

/// <summary>
/// The single writer of denormalized checkout badge state for a <see cref="WorkspaceFeatureContext"/>.
/// Special Workspace contexts also mirror onto <see cref="WorkspaceRepositoryLink"/> for legacy readers
/// until contract cleanup; Feature contexts write context state only.
/// </summary>
/// <remarks>
/// Every group of columns is written with replace semantics - a probed null really does clear the
/// column - but only when the snapshot says that group was probed. Groups a command does not inspect
/// are left exactly as they are, which is what lets a full post-checkout snapshot and a partial
/// commit-count refresh share one code path without either erasing the other's work.
/// <para>
/// The writer deliberately does not recompute workspace-wide dependency or file-version stats and
/// does not broadcast; that is <see cref="WorkspaceStateRecomputeScope"/>'s job, once per user action.
/// </para>
/// </remarks>
public sealed class WorkspaceRepositoryStateWriter(
    AppDbContext dbContext,
    RepositoryBranchWriter branchWriter,
    WorkspaceProjectRepository workspaceProjectRepository,
    WorkspacePullRequestService pullRequestService,
    IWorkspaceFeatureContextResolver contextResolver,
    IWorkspaceCapabilitiesResolver capabilitiesResolver,
    ILogger<WorkspaceRepositoryStateWriter> logger)
{
    /// <summary>
    /// Applies <paramref name="snapshot"/> to the special Workspace context (and mirrors onto the link).
    /// Prefer <see cref="ApplyAsync(WorkspaceFeatureContextId, int, int, RepositoryStateSnapshot, RepositoryStateWriteOptions?, CancellationToken)"/>
    /// when the caller already has an explicit context id.
    /// </summary>
    public async Task<bool> ApplyAsync(
        int workspaceId,
        int repositoryId,
        RepositoryStateSnapshot snapshot,
        RepositoryStateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var contextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        return await ApplyAsync(contextId, workspaceId, repositoryId, snapshot, options, cancellationToken);
    }

    /// <summary>Applies <paramref name="snapshot"/> to the given Feature context. Returns false when the link does not exist.</summary>
    public async Task<bool> ApplyAsync(
        WorkspaceFeatureContextId contextId,
        int workspaceId,
        int repositoryId,
        RepositoryStateSnapshot snapshot,
        RepositoryStateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new RepositoryStateWriteOptions();

        var context = await dbContext.WorkspaceFeatureContexts
            .FirstOrDefaultAsync(c => c.WorkspaceFeatureContextId == contextId.Value, cancellationToken);
        if (context is null || context.WorkspaceId != workspaceId)
        {
            logger.LogWarning(
                "State write skipped: context {ContextId} missing or not in workspace {WorkspaceId}",
                contextId.Value, workspaceId);
            return false;
        }

        var wr = await dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.RepositoryId == repositoryId, cancellationToken);
        if (wr == null)
        {
            logger.LogWarning("State write skipped: workspace {WorkspaceId} repository {RepositoryId} not linked", workspaceId, repositoryId);
            return false;
        }

        var state = await dbContext.WorkspaceRepositoryContextStates
            .FirstOrDefaultAsync(
                s => s.WorkspaceFeatureContextId == contextId.Value && s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId,
                cancellationToken);
        if (state is null)
        {
            state = new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = contextId.Value,
                WorkspaceRepositoryId = wr.WorkspaceRepositoryId,
                SyncStatus = RepoSyncStatus.NeedsSync
            };
            dbContext.WorkspaceRepositoryContextStates.Add(state);
        }

        var isSpecialWorkspace = context.Kind == WorkspaceFeatureContextKind.Workspace;
        var isReferenceSnapshot = !isSpecialWorkspace
            && (await LoadReferenceSnapshotLinkIdsAsync(contextId, [wr.WorkspaceRepositoryId], cancellationToken)).Count > 0;
        var previousBranch = StageTracked(context, wr, state, snapshot, options, isReferenceSnapshot);
        if (snapshot.BranchesProbed)
            StageTagRefresh(wr, state, isSpecialWorkspace, snapshot.Tags, snapshot.CheckedOutTag);

        await dbContext.SaveChangesAsync(cancellationToken);

        if (snapshot.BranchesProbed)
        {
            await branchWriter.PersistAsync(
                wr.WorkspaceRepositoryId,
                snapshot.LocalBranches,
                snapshot.RemoteBranches,
                snapshot.DefaultBranchName ?? wr.DefaultBranchName,
                snapshot.Tags,
                cancellationToken);
        }

        // Some Worker paths return projects without being asked (return-to-default, the hook fallback
        // when capabilities cannot be resolved); a workspace that does not discover projects keeps none.
        if (snapshot.ProjectsProbed
            && (await capabilitiesResolver.GetAsync(workspaceId, cancellationToken)).DiscoversDotNetProjects)
            await ApplyProjectsAsync(contextId, workspaceId, repositoryId, wr, state, isSpecialWorkspace, snapshot, cancellationToken);

        if (options.ReconcilePullRequest)
            await ReconcilePullRequestsAsync(contextId, workspaceId, [(wr, state, previousBranch)], isSpecialWorkspace, cancellationToken);

        return true;
    }

    /// <summary>
    /// Records which tag the context has checked out, from a refresh that listed the repository's tags
    /// (<paramref name="tags"/> newest first), and whether a newer tag exists. Nothing is written when
    /// <paramref name="tags"/> is null: the refresh did not look at tags. A Feature context writes its own context
    /// state only; the special Workspace also mirrors onto the link. Returns false when the context or the link is
    /// missing or they belong to different workspaces.
    /// </summary>
    public async Task<bool> ApplyTagRefreshAsync(
        WorkspaceFeatureContextId contextId,
        int workspaceRepositoryId,
        IReadOnlyList<string>? tags,
        string? currentTag,
        CancellationToken cancellationToken = default)
    {
        if (tags == null)
            return true;

        var context = await dbContext.WorkspaceFeatureContexts
            .FirstOrDefaultAsync(c => c.WorkspaceFeatureContextId == contextId.Value, cancellationToken);
        var wr = await dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(x => x.WorkspaceRepositoryId == workspaceRepositoryId, cancellationToken);
        if (context is null || wr is null || context.WorkspaceId != wr.WorkspaceId)
        {
            logger.LogWarning(
                "Tag refresh write skipped: context {ContextId} or workspace repository {WorkspaceRepositoryId} missing, or not in the same workspace",
                contextId.Value, workspaceRepositoryId);
            return false;
        }

        var state = await dbContext.WorkspaceRepositoryContextStates
            .FirstOrDefaultAsync(
                s => s.WorkspaceFeatureContextId == contextId.Value && s.WorkspaceRepositoryId == workspaceRepositoryId,
                cancellationToken);
        if (state is null)
        {
            // Nothing to clear on a context that has no state yet, and no row is created just to say "not on a tag".
            if (Blank(currentTag) && context.Kind != WorkspaceFeatureContextKind.Workspace)
                return true;
            state = new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = contextId.Value,
                WorkspaceRepositoryId = workspaceRepositoryId,
                SyncStatus = context.Kind == WorkspaceFeatureContextKind.Workspace ? wr.SyncStatus : RepoSyncStatus.NeedsSync,
                CheckedOutTag = context.Kind == WorkspaceFeatureContextKind.Workspace ? wr.CheckedOutTag : null,
            };
            dbContext.WorkspaceRepositoryContextStates.Add(state);
        }

        StageTagRefresh(wr, state, context.Kind == WorkspaceFeatureContextKind.Workspace, tags, currentTag);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Mutates the tracked context state and, for the special Workspace, the link. Does not save.</summary>
    private static void StageTagRefresh(
        WorkspaceRepositoryLink wr,
        WorkspaceRepositoryContextState state,
        bool isSpecialWorkspace,
        IReadOnlyList<string>? tags,
        string? currentTag)
    {
        if (tags == null)
            return;

        if (!Blank(currentTag))
        {
            state.CheckedOutTag = currentTag;
            state.BranchName = null;
            state.BranchHasUpstream = null;
            state.OutgoingCommits = null;
            state.IncomingCommits = null;
            state.DefaultBranchBehindCommits = null;
            state.DefaultBranchAheadCommits = null;
            // The list is newest first: any position after the first means a newer tag exists.
            var tagIndex = -1;
            for (var i = 0; i < tags.Count; i++)
            {
                if (string.Equals(tags[i], currentTag, StringComparison.OrdinalIgnoreCase))
                {
                    tagIndex = i;
                    break;
                }
            }

            state.HasNewerTag = tagIndex > 0;
        }
        else if (!Blank(state.CheckedOutTag) || (isSpecialWorkspace && !Blank(wr.CheckedOutTag)))
        {
            // Tags were refreshed and the checkout is no longer on one: clear the pinned state.
            state.CheckedOutTag = null;
            state.HasNewerTag = null;
        }
        else
        {
            return;
        }

        if (!isSpecialWorkspace)
            return;

        wr.CheckedOutTag = state.CheckedOutTag;
        wr.HasNewerTag = state.HasNewerTag;
        if (!Blank(state.CheckedOutTag))
        {
            wr.BranchName = null;
            wr.BranchHasUpstream = null;
            wr.OutgoingCommits = null;
            wr.IncomingCommits = null;
            wr.DefaultBranchBehindCommits = null;
            wr.DefaultBranchAheadCommits = null;
        }
    }

    private static void ApplyIdentity(WorkspaceRepositoryContextState state, RepositoryStateSnapshot snapshot)
    {
        if (!snapshot.IdentityProbed)
            return;

        if (!Blank(snapshot.CheckedOutTag))
        {
            state.CheckedOutTag = snapshot.CheckedOutTag;
            state.BranchName = null;
            state.HeadCommit = Blank(snapshot.HeadCommit) ? null : snapshot.HeadCommit;
            state.BranchHasUpstream = null;
            state.OutgoingCommits = null;
            state.IncomingCommits = null;
            state.DefaultBranchBehindCommits = null;
            state.DefaultBranchAheadCommits = null;
            return;
        }

        state.CheckedOutTag = null;
        state.HasNewerTag = null;
        state.BranchName = Blank(snapshot.BranchName) || snapshot.BranchName == "-" ? null : snapshot.BranchName;
        // Flat hook/sync payloads often omit HeadCommit; do not wipe a known SHA in that case.
        if (!Blank(snapshot.HeadCommit))
            state.HeadCommit = snapshot.HeadCommit;
    }

    private static void MirrorIdentityToLink(WorkspaceRepositoryLink wr, WorkspaceRepositoryContextState state)
    {
        // Identity only - commit counts / upstream are mirrored only when their probe groups run,
        // otherwise a stale context-state value would overwrite a just-mutated link field.
        wr.CheckedOutTag = state.CheckedOutTag;
        wr.HasNewerTag = state.HasNewerTag;
        wr.BranchName = state.BranchName;
    }

    private static void ApplySyncStatus(
        WorkspaceRepositoryContextState state,
        RepositoryStateSnapshot snapshot,
        RepositoryStateWriteOptions options,
        string? defaultBranchName,
        bool isReferenceSnapshot = false)
    {
        switch (options.SyncStatus)
        {
            case SyncStatusWrite.InSync:
                state.SyncStatus = RepoSyncStatus.InSync;
                return;
            case SyncStatusWrite.Error:
                state.SyncStatus = RepoSyncStatus.Error;
                return;
            case SyncStatusWrite.Leave:
                return;
        }

        if (options.ErrorMessageForcesInSync && !Blank(snapshot.ErrorMessage))
        {
            state.SyncStatus = RepoSyncStatus.InSync;
            return;
        }

        // A failed clone or fetch must not be derived back to InSync from a branch that was already on the row.
        // Hook notifications set ErrorMessageForcesInSync and do not set FatalSyncFailure, so they stay above.
        if (snapshot.FatalSyncFailure)
        {
            state.SyncStatus = snapshot.NotCloned ? RepoSyncStatus.NotCloned : RepoSyncStatus.Error;
            return;
        }

        // A repository is known once it has a branch or a tag. The GitVersion result is deliberately not part
        // of that: when GitVersion cannot compute a version (an empty repository, a broken config) the row shows
        // the version as unresolved, and the repository and the Workspace carry on as normal.
        // A reference snapshot has neither when it was taken from a branch: it is known by its commit.
        var hasIdentity = !Blank(state.BranchName)
            || !Blank(state.CheckedOutTag)
            || (isReferenceSnapshot && !Blank(state.HeadCommit));
        var hasDefaultBranch = !Blank(defaultBranchName);

        // A remote that has no branches at all (a freshly created, still empty hosted repository) has no
        // default branch to be out of sync with, so a completed sync must not leave the repository on NeedsSync.
        var remoteIsEmpty = snapshot.RemoteBranches is { Count: 0 };

        // Hook notifications (a commit made in a terminal) say nothing about the remote's branches, so they cannot
        // learn a default branch. They must not take away an in-sync status an earlier full sync established.
        var knowsNothingAboutRemote = snapshot.RemoteBranches is null;
        var keepsEarlierSync = knowsNothingAboutRemote && state.SyncStatus == RepoSyncStatus.InSync;

        state.SyncStatus = !hasIdentity
            ? RepoSyncStatus.Error
            : hasDefaultBranch || remoteIsEmpty || keepsEarlierSync ? RepoSyncStatus.InSync : RepoSyncStatus.NeedsSync;
    }

    private async Task ApplyProjectsAsync(
        WorkspaceFeatureContextId contextId,
        int workspaceId,
        int repositoryId,
        WorkspaceRepositoryLink wr,
        WorkspaceRepositoryContextState state,
        bool isSpecialWorkspace,
        RepositoryStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var projects = ToSyncProjects(snapshot.Projects);

        state.Projects = projects.Count;
        state.RepositoryType = ComputeRepositoryType(projects);
        if (isSpecialWorkspace)
        {
            wr.Projects = state.Projects;
            wr.RepositoryType = state.RepositoryType;
        }

        // An empty list from a probed scan is meaningful: the branch now checked out genuinely has no
        // projects, so the previous branch's projects (and, by cascade, their dependency edges) go away.
        // The staged state above, the projects and the edges commit together.
        await workspaceProjectRepository.ReconcileProjectSnapshotAsync(
            workspaceId,
            contextId.Value,
            [ProjectScanOutcome.Scanned(repositoryId, projects)],
            mergeEdges: true,
            persistDependencyLevel: false,
            cancellationToken);
    }

    /// <summary>
    /// Reconciles the persisted pull request of every staged repository against the branch now checked out.
    /// All GitHub lookups go out in one refresh call so they fan out in parallel (bounded by
    /// <see cref="WorkspaceOptions.MaxParallelOperations"/>) instead of one repository at a time.
    /// </summary>
    private async Task ReconcilePullRequestsAsync(
        WorkspaceFeatureContextId contextId,
        int workspaceId,
        IReadOnlyList<(WorkspaceRepositoryLink Wr, WorkspaceRepositoryContextState State, string? PreviousBranch)> staged,
        bool isSpecialWorkspace,
        CancellationToken cancellationToken)
    {
        var branchByRepositoryId = new Dictionary<int, string?>();
        foreach (var (wr, state, previousBranch) in staged)
        {
            // Evicting the cached lookup is what forces a fresh GitHub call for a repository whose branch
            // changed, so the batched refresh below does not need a per-repository force flag.
            if (!string.Equals(previousBranch, state.BranchName, StringComparison.Ordinal))
                pullRequestService.EvictCacheForRepository(wr.RepositoryId);

            var branch = state.BranchName;
            var isOnDefault = !Blank(branch) && !Blank(wr.DefaultBranchName)
                && string.Equals(branch, wr.DefaultBranchName, StringComparison.OrdinalIgnoreCase);

            if (!isSpecialWorkspace)
            {
                // A Feature context's own PR projection (§17) is refreshed against its own checked-out branch and
                // written only to WorkspaceRepositoryContextPullRequests - never to wr.BranchName or the legacy
                // per-link PR row, which stay reserved for the special Workspace (see the data-corruption note this
                // guard replaced: a Feature sync used to silently overwrite the Workspace's own branch/PR display).
                if (Blank(branch) || isOnDefault)
                    await pullRequestService.ClearContextPullRequestAsync(contextId.Value, wr.WorkspaceRepositoryId, cancellationToken);
                else
                    branchByRepositoryId[wr.RepositoryId] = branch;
                continue;
            }

            wr.BranchName = state.BranchName;

            if (Blank(branch) || isOnDefault)
                await pullRequestService.ClearPullRequestAsync(workspaceId, wr.RepositoryId, cancellationToken);
            else
                branchByRepositoryId[wr.RepositoryId] = branch;
        }

        if (branchByRepositoryId.Count == 0)
            return;

        if (isSpecialWorkspace)
            await pullRequestService.RefreshPullRequestsAsync(workspaceId, branchByRepositoryId.Keys.ToList(), force: false, cancellationToken);
        else
            await pullRequestService.RefreshContextPullRequestsAsync(workspaceId, contextId.Value, branchByRepositoryId, force: false, cancellationToken);
    }

    /// <summary>Dominant project type for the repository: Service &gt; Package &gt; Executable &gt; Library &gt; Test.</summary>
    public static ProjectType? ComputeRepositoryType(IReadOnlyList<SyncProjectInfo>? projects)
    {
        if (projects == null || projects.Count == 0) return null;
        if (projects.Any(p => p.ProjectType == ProjectType.Service)) return ProjectType.Service;
        if (projects.Any(p => p.ProjectType == ProjectType.Package)) return ProjectType.Package;
        if (projects.Any(p => p.ProjectType == ProjectType.Executable)) return ProjectType.Executable;
        if (projects.Any(p => p.ProjectType == ProjectType.Library)) return ProjectType.Library;
        return ProjectType.Test;
    }

    /// <summary>Maps the wire project shape onto the App's persistence model.</summary>
    public static List<SyncProjectInfo> ToSyncProjects(IReadOnlyList<RepositorySyncProjectNotification>? projects)
    {
        if (projects == null)
            return [];
        return projects
            .Where(p => !Blank(p.Name))
            .Select(p => new SyncProjectInfo(
                p.Name.Trim(),
                p.ProjectType is >= 0 and <= 4 ? (ProjectType)p.ProjectType : ProjectType.Library,
                p.ProjectPath ?? "",
                p.TargetFramework ?? "",
                p.PackageId,
                (p.PackageReferences ?? [])
                    .Where(pr => !Blank(pr.Name))
                    .Select(pr => new SyncPackageReference(pr.Name.Trim(), pr.Version ?? ""))
                    .ToList()))
            .ToList();
    }

    /// <summary>
    /// Applies every snapshot on this writer's context, then one <see cref="AppDbContext.SaveChangesAsync"/>
    /// (or the project-row batch save, which flushes the same context). Does not merge dependency edges;
    /// callers that already have the full graph call <c>MergeWorkspaceProjectDependenciesAsync</c> once after.
    /// </summary>
    public async Task ApplyBatchAsync(
        WorkspaceFeatureContextId contextId,
        int workspaceId,
        IReadOnlyList<(int RepositoryId, RepositoryStateSnapshot Snapshot)> items,
        RepositoryStateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return;

        options ??= new RepositoryStateWriteOptions();

        var context = await dbContext.WorkspaceFeatureContexts
            .FirstOrDefaultAsync(c => c.WorkspaceFeatureContextId == contextId.Value, cancellationToken);
        if (context is null || context.WorkspaceId != workspaceId)
        {
            logger.LogWarning(
                "State write skipped: context {ContextId} missing or not in workspace {WorkspaceId}",
                contextId.Value, workspaceId);
            return;
        }

        var repoIds = items.Select(i => i.RepositoryId).Distinct().ToList();
        var links = await dbContext.WorkspaceRepositories
            .Where(x => x.WorkspaceId == workspaceId && repoIds.Contains(x.RepositoryId))
            .ToListAsync(cancellationToken);
        var linkByRepo = links.ToDictionary(l => l.RepositoryId);
        var wrlIds = links.Select(l => l.WorkspaceRepositoryId).ToList();
        var states = await dbContext.WorkspaceRepositoryContextStates
            .Where(s => s.WorkspaceFeatureContextId == contextId.Value && wrlIds.Contains(s.WorkspaceRepositoryId))
            .ToListAsync(cancellationToken);
        var stateByWrl = states.ToDictionary(s => s.WorkspaceRepositoryId);
        var discoversProjects = (await capabilitiesResolver.GetAsync(workspaceId, cancellationToken)).DiscoversDotNetProjects;
        var isSpecialWorkspace = context.Kind == WorkspaceFeatureContextKind.Workspace;
        var referenceSnapshotLinkIds = isSpecialWorkspace
            ? []
            : await LoadReferenceSnapshotLinkIdsAsync(contextId, wrlIds, cancellationToken);
        var staged = new List<(WorkspaceRepositoryLink Wr, WorkspaceRepositoryContextState State, string? PreviousBranch)>();
        var projectBatch = new List<(int RepositoryId, IReadOnlyList<SyncProjectInfo> Projects)>();

        foreach (var (repositoryId, snapshot) in items)
        {
            if (!linkByRepo.TryGetValue(repositoryId, out var wr))
            {
                logger.LogWarning("State write skipped: workspace {WorkspaceId} repository {RepositoryId} not linked", workspaceId, repositoryId);
                continue;
            }

            if (!stateByWrl.TryGetValue(wr.WorkspaceRepositoryId, out var state))
            {
                state = new WorkspaceRepositoryContextState
                {
                    WorkspaceFeatureContextId = contextId.Value,
                    WorkspaceRepositoryId = wr.WorkspaceRepositoryId,
                    SyncStatus = RepoSyncStatus.NeedsSync
                };
                dbContext.WorkspaceRepositoryContextStates.Add(state);
                stateByWrl[wr.WorkspaceRepositoryId] = state;
            }

            var previousBranch = StageTracked(
                context, wr, state, snapshot, options, referenceSnapshotLinkIds.Contains(wr.WorkspaceRepositoryId));
            staged.Add((wr, state, previousBranch));

            if (snapshot.BranchesProbed)
            {
                StageTagRefresh(wr, state, isSpecialWorkspace, snapshot.Tags, snapshot.CheckedOutTag);
                await branchWriter.PersistAsync(
                    wr.WorkspaceRepositoryId,
                    snapshot.LocalBranches,
                    snapshot.RemoteBranches,
                    snapshot.DefaultBranchName ?? wr.DefaultBranchName,
                    snapshot.Tags,
                    cancellationToken,
                    save: false);
            }

            if (snapshot.ProjectsProbed && discoversProjects)
            {
                var projects = ToSyncProjects(snapshot.Projects);
                projectBatch.Add((repositoryId, projects));
                state.Projects = projects.Count;
                state.RepositoryType = ComputeRepositoryType(projects);
                if (isSpecialWorkspace)
                {
                    wr.Projects = state.Projects;
                    wr.RepositoryType = state.RepositoryType;
                }
            }
        }

        if (projectBatch.Count > 0)
        {
            // Saves this context, including the staged checkout rows and branch refs, in the same transaction as the project merge.
            // The edges are merged for the whole graph by the caller (see WorkspaceGitService.PersistVersionsAsync).
            await workspaceProjectRepository.ReconcileProjectSnapshotAsync(
                workspaceId,
                contextId.Value,
                projectBatch.Select(b => ProjectScanOutcome.Scanned(b.RepositoryId, b.Projects)).ToList(),
                mergeEdges: false,
                persistDependencyLevel: false,
                cancellationToken);
        }
        else
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (options.ReconcilePullRequest)
            await ReconcilePullRequestsAsync(contextId, workspaceId, staged, isSpecialWorkspace, cancellationToken);
    }

    /// <summary>
    /// The links, of those given, that the Feature context holds as reference snapshots. A snapshot is detached at
    /// a commit by design: it has no branch, and one taken from a branch has no tag either.
    /// </summary>
    private async Task<HashSet<int>> LoadReferenceSnapshotLinkIdsAsync(
        WorkspaceFeatureContextId contextId,
        IReadOnlyCollection<int> workspaceRepositoryIds,
        CancellationToken cancellationToken) =>
        (await dbContext.WorkspaceFeatureRepositories
            .AsNoTracking()
            .Where(row => row.WorkspaceFeatureContextId == contextId.Value
                && row.IsReferenceSnapshot
                && workspaceRepositoryIds.Contains(row.WorkspaceRepositoryId))
            .Select(row => row.WorkspaceRepositoryId)
            .ToListAsync(cancellationToken))
        .ToHashSet();

    /// <summary>Mutates the tracked context state and, for the special Workspace, the link. Does not save.</summary>
    private static string? StageTracked(
        WorkspaceFeatureContext context,
        WorkspaceRepositoryLink wr,
        WorkspaceRepositoryContextState state,
        RepositoryStateSnapshot snapshot,
        RepositoryStateWriteOptions options,
        bool isReferenceSnapshot = false)
    {
        var previousBranch = state.BranchName;
        var isSpecialWorkspace = context.Kind == WorkspaceFeatureContextKind.Workspace;

        ApplyIdentity(state, snapshot);

        // Whatever a probe calls a detached HEAD ("HEAD", a description of the commit), a snapshot has no branch.
        if (isReferenceSnapshot && snapshot.IdentityProbed)
            state.BranchName = null;

        if (isSpecialWorkspace && snapshot.IdentityProbed)
        {
            MirrorIdentityToLink(wr, state);
            if (!string.IsNullOrWhiteSpace(state.CheckedOutTag))
            {
                wr.BranchHasUpstream = null;
                wr.OutgoingCommits = null;
                wr.IncomingCommits = null;
                wr.DefaultBranchBehindCommits = null;
                wr.DefaultBranchAheadCommits = null;
            }
        }

        if (snapshot.GitVersionProbed)
        {
            state.GitVersion = Blank(snapshot.GitVersion) ? null : snapshot.GitVersion;
            // A probe result, even a failed one (null version = unresolved), ends the Feature seed's pending state.
            state.GitVersionPending = false;
            if (isSpecialWorkspace)
                wr.GitVersion = state.GitVersion;
        }

        // A tag checkout and a reference snapshot are both detached: there is no branch to count commits for.
        var onTag = !string.IsNullOrWhiteSpace(state.CheckedOutTag) || isReferenceSnapshot;
        if (!onTag)
        {
            if (snapshot.CommitCountsProbed)
            {
                state.OutgoingCommits = snapshot.OutgoingCommits;
                state.IncomingCommits = snapshot.IncomingCommits;
                if (snapshot.DefaultBranchBehind.HasValue || snapshot.DefaultBranchAhead.HasValue)
                {
                    state.DefaultBranchBehindCommits = snapshot.DefaultBranchBehind;
                    state.DefaultBranchAheadCommits = snapshot.DefaultBranchAhead;
                }

                if (isSpecialWorkspace)
                {
                    wr.OutgoingCommits = state.OutgoingCommits;
                    wr.IncomingCommits = state.IncomingCommits;
                    wr.DefaultBranchBehindCommits = state.DefaultBranchBehindCommits;
                    wr.DefaultBranchAheadCommits = state.DefaultBranchAheadCommits;
                }
            }

            if (snapshot.UpstreamProbed)
            {
                state.BranchHasUpstream = snapshot.HasUpstream;
                if (isSpecialWorkspace)
                    wr.BranchHasUpstream = state.BranchHasUpstream;
            }
        }

        // Default branch name is repository/ref metadata shared across contexts - keep on the link.
        if (!Blank(snapshot.DefaultBranchName))
            wr.DefaultBranchName = snapshot.DefaultBranchName;

        ApplySyncStatus(state, snapshot, options, wr.DefaultBranchName, isReferenceSnapshot);
        if (isSpecialWorkspace)
            wr.SyncStatus = state.SyncStatus;

        return previousBranch;
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
