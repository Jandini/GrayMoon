using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

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

    /// <summary>
    /// <see cref="RepositoryStateFreshness.Now"/> read before the Worker command that produced the snapshot was
    /// sent. When set, a hook snapshot the App received before that moment can no longer overwrite the groups
    /// this write probed. Null for writes that are not the result of an operation.
    /// </summary>
    public long? OperationStartedAt { get; init; }

    /// <summary>
    /// Leave the snapshot's projects unwritten. For a caller that writes several repositories at the same time and
    /// reconciles all their projects afterwards in one <see cref="WorkspaceRepositoryStateWriter.ApplyProjectsAsync"/> call.
    /// </summary>
    public bool DeferProjects { get; init; }
}

/// <summary>
/// What one state write did. <see cref="Probed"/> is what the snapshot claimed to have read; <see cref="Changed"/> is
/// what was different from the stored state. A write that confirms what is stored changes nothing, and what
/// depends on those facts (versions, dependency levels, file checks, the grid) has nothing to follow.
/// </summary>
/// <param name="Applied">False when the context or the link did not exist, so nothing was written.</param>
public readonly record struct RepositoryStateWriteResult(bool Applied, RepositoryFactGroups Probed, RepositoryFactGroups Changed)
{
    public static RepositoryStateWriteResult NotApplied { get; } = new(false, RepositoryFactGroups.None, RepositoryFactGroups.None);

    /// <summary>Existing callers ask "was it written": that is <see cref="Applied"/>.</summary>
    public static implicit operator bool(RepositoryStateWriteResult result) => result.Applied;
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
    ILogger<WorkspaceRepositoryStateWriter> logger,
    RepositoryStateFreshness? freshness = null)
{
    /// <summary>
    /// Applies <paramref name="snapshot"/> to the special Workspace context (and mirrors onto the link).
    /// Prefer <see cref="ApplyAsync(WorkspaceFeatureContextId, int, int, RepositoryStateSnapshot, RepositoryStateWriteOptions?, CancellationToken)"/>
    /// when the caller already has an explicit context id.
    /// </summary>
    public async Task<RepositoryStateWriteResult> ApplyAsync(
        int workspaceId,
        int repositoryId,
        RepositoryStateSnapshot snapshot,
        RepositoryStateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var contextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        return await ApplyAsync(contextId, workspaceId, repositoryId, snapshot, options, cancellationToken);
    }

    /// <summary>
    /// Applies <paramref name="snapshot"/> to the given context and reports which groups it changed. Not applied
    /// (converts to false) when the context or the link does not exist: a report for a context that was deleted
    /// writes nothing and creates no row.
    /// </summary>
    public async Task<RepositoryStateWriteResult> ApplyAsync(
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
            return RepositoryStateWriteResult.NotApplied;
        }

        var wr = await dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.RepositoryId == repositoryId, cancellationToken);
        if (wr == null)
        {
            logger.LogWarning("State write skipped: workspace {WorkspaceId} repository {RepositoryId} not linked", workspaceId, repositoryId);
            return RepositoryStateWriteResult.NotApplied;
        }

        var state = await dbContext.WorkspaceRepositoryContextStates
            .FirstOrDefaultAsync(
                s => s.WorkspaceFeatureContextId == contextId.Value && s.WorkspaceRepositoryId == wr.WorkspaceRepositoryId,
                cancellationToken);
        LinkBeforeStaging? linkBeforeFirstRow = null;
        if (state is null)
        {
            state = new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = contextId.Value,
                WorkspaceRepositoryId = wr.WorkspaceRepositoryId,
                SyncStatus = RepoSyncStatus.NeedsSync
            };
            dbContext.WorkspaceRepositoryContextStates.Add(state);
            linkBeforeFirstRow = LinkBeforeStaging.Capture(dbContext.Entry(wr));
        }

        var isSpecialWorkspace = context.Kind == WorkspaceFeatureContextKind.Workspace;
        var isReferenceSnapshot = !isSpecialWorkspace
            && (await LoadReferenceSnapshotLinkIdsAsync(contextId, [wr.WorkspaceRepositoryId], cancellationToken)).Count > 0;
        var probed = RepositoryStateFreshness.ProbedGroups(snapshot);
        var isNewState = false;
        string? previousBranch = null;
        var changed = RepositoryFactGroups.None;

        void Stage()
        {
            // A new row has nothing stored to compare with: everything the snapshot probed is a change.
            isNewState = dbContext.Entry(state).State == EntityState.Added;
            var before = TrackedValues.Read(state, wr);
            previousBranch = StageTracked(context, wr, state, snapshot, options, isReferenceSnapshot);
            if (snapshot.BranchesProbed)
                StageTagRefresh(wr, state, isSpecialWorkspace, snapshot.Tags, snapshot.CheckedOutTag);
            changed = isNewState ? probed & RepositoryFactGroups.Checkout : TrackedValues.Diff(before, TrackedValues.Read(state, wr));
        }

        Stage();
        await SaveStagedAsync(
            linkBeforeFirstRow is null ? [] : [state],
            stored =>
            {
                // Another writer stored the first row meanwhile: the write is staged again, on that row.
                linkBeforeFirstRow!.Restore(dbContext.Entry(wr));
                state = stored;
                Stage();
            },
            cancellationToken);

        if (snapshot.BranchesProbed)
        {
            if (await branchWriter.PersistAsync(
                wr.WorkspaceRepositoryId,
                snapshot.LocalBranches,
                snapshot.RemoteBranches,
                snapshot.DefaultBranchName ?? wr.DefaultBranchName,
                snapshot.Tags,
                cancellationToken))
                changed |= RepositoryFactGroups.Branches;
        }

        if (options.OperationStartedAt is { } operationStartedAt)
            freshness?.RecordOperationWrite(contextId, repositoryId, snapshot, operationStartedAt, workspaceId);
        freshness?.RecordApplied(contextId, repositoryId, snapshot, workspaceId);

        // Some Worker paths return projects without being asked (return-to-default, the hook fallback
        // when capabilities cannot be resolved); a workspace that does not discover projects keeps none.
        if (snapshot.ProjectsProbed && options.DeferProjects)
        {
            // The caller reconciles the projects itself, later: what that changes is not known here.
            changed |= RepositoryFactGroups.Projects;
        }
        else if (snapshot.ProjectsProbed
            && (await capabilitiesResolver.GetAsync(workspaceId, cancellationToken)).DiscoversDotNetProjects)
        {
            // Most scans confirm the stored projects. The rows and edges are compared before and after the reconcile,
            // so a scan that changed none of them is not reported as a change.
            var projectCountBefore = (state.Projects, state.RepositoryType);
            var projectsBefore = isNewState
                ? null
                : await workspaceProjectRepository.ReadProjectFingerprintAsync(contextId.Value, repositoryId, cancellationToken);
            await ApplyProjectsAsync(contextId, workspaceId, repositoryId, wr, state, isSpecialWorkspace, snapshot, cancellationToken);
            if (projectsBefore is null
                || projectCountBefore != (state.Projects, state.RepositoryType)
                || !projectsBefore.SetEquals(await workspaceProjectRepository.ReadProjectFingerprintAsync(contextId.Value, repositoryId, cancellationToken)))
                changed |= RepositoryFactGroups.Projects;
        }

        if (options.ReconcilePullRequest)
            await ReconcilePullRequestsAsync(contextId, workspaceId, [(wr, state, previousBranch)], isSpecialWorkspace, cancellationToken);

        return new RepositoryStateWriteResult(true, probed, changed);
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

        var isSpecialWorkspace = context.Kind == WorkspaceFeatureContextKind.Workspace;
        var state = await dbContext.WorkspaceRepositoryContextStates
            .FirstOrDefaultAsync(
                s => s.WorkspaceFeatureContextId == contextId.Value && s.WorkspaceRepositoryId == workspaceRepositoryId,
                cancellationToken);
        LinkBeforeStaging? linkBeforeFirstRow = null;
        if (state is null)
        {
            // Nothing to clear on a context that has no state yet, and no row is created just to say "not on a tag".
            if (Blank(currentTag) && !isSpecialWorkspace)
                return true;
            state = new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = contextId.Value,
                WorkspaceRepositoryId = workspaceRepositoryId,
                SyncStatus = isSpecialWorkspace ? wr.SyncStatus : RepoSyncStatus.NeedsSync,
                CheckedOutTag = isSpecialWorkspace ? wr.CheckedOutTag : null,
            };
            dbContext.WorkspaceRepositoryContextStates.Add(state);
            linkBeforeFirstRow = LinkBeforeStaging.Capture(dbContext.Entry(wr));
        }

        StageTagRefresh(wr, state, isSpecialWorkspace, tags, currentTag);
        await SaveStagedAsync(
            linkBeforeFirstRow is null ? [] : [state],
            stored =>
            {
                // Another writer stored the first row meanwhile: the refresh is staged again, on that row.
                linkBeforeFirstRow!.Restore(dbContext.Entry(wr));
                StageTagRefresh(wr, stored, isSpecialWorkspace, tags, currentTag);
            },
            cancellationToken);
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
            state.HeadCommit = Blank(snapshot.HeadCommit) ? null : snapshot.HeadCommit!.Trim();
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
        // Replace semantics, like every probed group: a probed identity without a commit (a repository with no
        // commits) clears the stored one. Keeping it left the commit of a checkout that is no longer there, and the
        // file-version check and the effective version both decide from this column.
        state.HeadCommit = Blank(snapshot.HeadCommit) ? null : snapshot.HeadCommit!.Trim();
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
    /// Writes the projects of several repositories of one context as a single projection: project rows, project
    /// counts and dependency edges commit together. For snapshots whose state was written with
    /// <see cref="RepositoryStateWriteOptions.DeferProjects"/>. Snapshots that did not probe projects and
    /// repositories that are not linked are left out; a workspace that does not discover projects writes nothing.
    /// </summary>
    public async Task ApplyProjectsAsync(
        WorkspaceFeatureContextId contextId,
        int workspaceId,
        IReadOnlyList<(int RepositoryId, RepositoryStateSnapshot Snapshot)> items,
        CancellationToken cancellationToken = default)
    {
        var probed = items.Where(i => i.Snapshot.ProjectsProbed).ToList();
        if (probed.Count == 0
            || !(await capabilitiesResolver.GetAsync(workspaceId, cancellationToken)).DiscoversDotNetProjects)
            return;

        var context = await dbContext.WorkspaceFeatureContexts
            .FirstOrDefaultAsync(c => c.WorkspaceFeatureContextId == contextId.Value, cancellationToken);
        if (context is null || context.WorkspaceId != workspaceId)
        {
            logger.LogWarning(
                "Project write skipped: context {ContextId} missing or not in workspace {WorkspaceId}",
                contextId.Value, workspaceId);
            return;
        }

        var repoIds = probed.Select(i => i.RepositoryId).Distinct().ToList();
        var links = await dbContext.WorkspaceRepositories
            .Where(x => x.WorkspaceId == workspaceId && repoIds.Contains(x.RepositoryId))
            .ToListAsync(cancellationToken);
        var linkByRepo = links.ToDictionary(l => l.RepositoryId);
        var wrlIds = links.Select(l => l.WorkspaceRepositoryId).ToList();
        var stateByWrl = (await dbContext.WorkspaceRepositoryContextStates
                .Where(s => s.WorkspaceFeatureContextId == contextId.Value && wrlIds.Contains(s.WorkspaceRepositoryId))
                .ToListAsync(cancellationToken))
            .ToDictionary(s => s.WorkspaceRepositoryId);
        var isSpecialWorkspace = context.Kind == WorkspaceFeatureContextKind.Workspace;

        var outcomes = new List<ProjectScanOutcome>();
        foreach (var (repositoryId, snapshot) in probed)
        {
            if (!linkByRepo.TryGetValue(repositoryId, out var wr)
                || !stateByWrl.TryGetValue(wr.WorkspaceRepositoryId, out var state))
                continue;

            var projects = ToSyncProjects(snapshot.Projects);
            state.Projects = projects.Count;
            state.RepositoryType = ComputeRepositoryType(projects);
            if (isSpecialWorkspace)
            {
                wr.Projects = state.Projects;
                wr.RepositoryType = state.RepositoryType;
            }

            outcomes.Add(ProjectScanOutcome.Scanned(repositoryId, projects));
        }

        if (outcomes.Count == 0)
            return;

        await workspaceProjectRepository.ReconcileProjectSnapshotAsync(
            workspaceId,
            contextId.Value,
            outcomes,
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
    /// Only when a batch both stores the first state row of a repository and carries projects, the staged rows are
    /// saved before the project reconcile instead of with it: a first row can lose a race to another writer
    /// (see <see cref="SaveStagedAsync"/>), and a save that failed inside the reconcile cannot be repeated there.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, RepositoryStateWriteResult>> ApplyBatchAsync(
        WorkspaceFeatureContextId contextId,
        int workspaceId,
        IReadOnlyList<(int RepositoryId, RepositoryStateSnapshot Snapshot)> items,
        RepositoryStateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<int, RepositoryStateWriteResult>();
        if (items.Count == 0)
            return results;

        options ??= new RepositoryStateWriteOptions();

        var context = await dbContext.WorkspaceFeatureContexts
            .FirstOrDefaultAsync(c => c.WorkspaceFeatureContextId == contextId.Value, cancellationToken);
        if (context is null || context.WorkspaceId != workspaceId)
        {
            logger.LogWarning(
                "State write skipped: context {ContextId} missing or not in workspace {WorkspaceId}",
                contextId.Value, workspaceId);
            return results;
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
        var passes = new List<BatchPass>();
        var firstRows = new List<WorkspaceRepositoryContextState>();
        var linkBeforeFirstRow = new Dictionary<int, LinkBeforeStaging>();

        // Stages one item on the state row its link has now. Run again for a link whose first row another writer stored.
        void Stage(BatchPass pass)
        {
            var wr = pass.Wr;
            var snapshot = pass.Snapshot;
            var state = stateByWrl[wr.WorkspaceRepositoryId];
            var isNewState = dbContext.Entry(state).State == EntityState.Added;
            var before = TrackedValues.Read(state, wr);
            pass.PreviousBranch = StageTracked(
                context, wr, state, snapshot, options, referenceSnapshotLinkIds.Contains(wr.WorkspaceRepositoryId));
            if (snapshot.BranchesProbed)
                StageTagRefresh(wr, state, isSpecialWorkspace, snapshot.Tags, snapshot.CheckedOutTag);
            pass.Changed = isNewState
                ? RepositoryStateFreshness.ProbedGroups(snapshot) & RepositoryFactGroups.Checkout
                : TrackedValues.Diff(before, TrackedValues.Read(state, wr));

            if (pass.Projects is { } projects)
            {
                state.Projects = projects.Count;
                state.RepositoryType = ComputeRepositoryType(projects);
                if (isSpecialWorkspace)
                {
                    wr.Projects = state.Projects;
                    wr.RepositoryType = state.RepositoryType;
                }
            }
        }

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
                firstRows.Add(state);
                linkBeforeFirstRow[wr.WorkspaceRepositoryId] = LinkBeforeStaging.Capture(dbContext.Entry(wr));
            }

            var pass = new BatchPass(
                repositoryId,
                snapshot,
                wr,
                snapshot.ProjectsProbed && discoversProjects ? ToSyncProjects(snapshot.Projects) : null);
            passes.Add(pass);
            Stage(pass);

            if (snapshot.BranchesProbed)
            {
                pass.BranchesChanged = await branchWriter.PersistAsync(
                    wr.WorkspaceRepositoryId,
                    snapshot.LocalBranches,
                    snapshot.RemoteBranches,
                    snapshot.DefaultBranchName ?? wr.DefaultBranchName,
                    snapshot.Tags,
                    cancellationToken,
                    save: false);
            }

            if (pass.Projects is not null)
                projectBatch.Add((repositoryId, pass.Projects));
        }

        // Another writer stored the first row of a link meanwhile: every item of that link is staged again, on that
        // row. The branch rows and the projects do not depend on the state row and stay staged as they are.
        void StageAgainOnStoredRow(WorkspaceRepositoryContextState stored)
        {
            var linkId = stored.WorkspaceRepositoryId;
            stateByWrl[linkId] = stored;
            var linkPasses = passes.Where(p => p.Wr.WorkspaceRepositoryId == linkId).ToList();
            if (linkPasses.Count == 0)
                return;

            linkBeforeFirstRow[linkId].Restore(dbContext.Entry(linkPasses[0].Wr));
            foreach (var pass in linkPasses)
                Stage(pass);
        }

        if (projectBatch.Count > 0)
        {
            // A first state row is stored before the reconcile: its insert can lose a race, and the reconcile's save,
            // made under the projection gate with the project rows it decided there, cannot be repeated from here.
            if (firstRows.Count > 0)
                await SaveStagedAsync(firstRows, StageAgainOnStoredRow, cancellationToken);

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
            await SaveStagedAsync(firstRows, StageAgainOnStoredRow, cancellationToken);
        }

        foreach (var pass in passes)
        {
            var changed = pass.Changed;
            if (pass.BranchesChanged)
                changed |= RepositoryFactGroups.Branches;
            if (pass.Snapshot.ProjectsProbed)
                changed |= RepositoryFactGroups.Projects;
            results[pass.RepositoryId] = new RepositoryStateWriteResult(true, RepositoryStateFreshness.ProbedGroups(pass.Snapshot), changed);
            staged.Add((pass.Wr, stateByWrl[pass.Wr.WorkspaceRepositoryId], pass.PreviousBranch));
        }

        if (freshness is not null)
        {
            foreach (var (repositoryId, snapshot) in items)
            {
                if (!linkByRepo.ContainsKey(repositoryId))
                    continue;
                if (options.OperationStartedAt is { } operationStartedAt)
                    freshness.RecordOperationWrite(contextId, repositoryId, snapshot, operationStartedAt, workspaceId);
                freshness.RecordApplied(contextId, repositoryId, snapshot, workspaceId);
            }
        }

        if (options.ReconcilePullRequest)
            await ReconcilePullRequestsAsync(contextId, workspaceId, staged, isSpecialWorkspace, cancellationToken);
        return results;
    }

    /// <summary>The stored values a state write can change, read before and after it to report what changed.</summary>
    private readonly record struct TrackedValues(
        string? Branch,
        string? Tag,
        string? Head,
        bool? HasNewerTag,
        string? GitVersion,
        bool? GitVersionPending,
        int? Outgoing,
        int? Incoming,
        int? DefaultBehind,
        int? DefaultAhead,
        bool? HasUpstream,
        RepoSyncStatus SyncStatus,
        string? DefaultBranch)
    {
        public static TrackedValues Read(WorkspaceRepositoryContextState state, WorkspaceRepositoryLink wr) => new(
            state.BranchName, state.CheckedOutTag, state.HeadCommit, state.HasNewerTag,
            state.GitVersion, state.GitVersionPending,
            state.OutgoingCommits, state.IncomingCommits, state.DefaultBranchBehindCommits, state.DefaultBranchAheadCommits,
            state.BranchHasUpstream, state.SyncStatus, wr.DefaultBranchName);

        public static RepositoryFactGroups Diff(TrackedValues before, TrackedValues after)
        {
            var changed = RepositoryFactGroups.None;
            // The sync status and the default branch name are shown with the identity and follow it.
            if (before.Branch != after.Branch || before.Tag != after.Tag || before.Head != after.Head || before.HasNewerTag != after.HasNewerTag
                || before.SyncStatus != after.SyncStatus || before.DefaultBranch != after.DefaultBranch)
                changed |= RepositoryFactGroups.Identity;
            if (before.GitVersion != after.GitVersion || before.GitVersionPending != after.GitVersionPending)
                changed |= RepositoryFactGroups.GitVersion;
            if (before.Outgoing != after.Outgoing || before.Incoming != after.Incoming
                || before.DefaultBehind != after.DefaultBehind || before.DefaultAhead != after.DefaultAhead)
                changed |= RepositoryFactGroups.CommitCounts;
            if (before.HasUpstream != after.HasUpstream)
                changed |= RepositoryFactGroups.Upstream;
            return changed;
        }
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

    /// <summary>One item of a batch: what was asked for, and what staging it found.</summary>
    private sealed class BatchPass(
        int repositoryId,
        RepositoryStateSnapshot snapshot,
        WorkspaceRepositoryLink wr,
        IReadOnlyList<SyncProjectInfo>? projects)
    {
        public int RepositoryId { get; } = repositoryId;
        public RepositoryStateSnapshot Snapshot { get; } = snapshot;
        public WorkspaceRepositoryLink Wr { get; } = wr;

        /// <summary>The projects to store, or null when the item probed none or the Workspace keeps none.</summary>
        public IReadOnlyList<SyncProjectInfo>? Projects { get; } = projects;

        public string? PreviousBranch { get; set; }
        public RepositoryFactGroups Changed { get; set; }
        public bool BranchesChanged { get; set; }
    }

    /// <summary>
    /// The pending changes of a link before a write staged its mirror for a new state row. When that row loses the
    /// race, the write is staged again on the stored row, and what the first staging put on the link must not stay
    /// behind where the second one does not write it (commit counts staged for a row that turns out to be on a tag).
    /// Changes the caller left pending on the link are kept.
    /// </summary>
    private sealed class LinkBeforeStaging(IReadOnlyDictionary<string, object?> pendingValues)
    {
        public static LinkBeforeStaging Capture(EntityEntry<WorkspaceRepositoryLink> link) => new(
            link.Properties
                .Where(p => p.IsModified)
                .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue));

        public void Restore(EntityEntry<WorkspaceRepositoryLink> link)
        {
            foreach (var property in link.Properties)
            {
                if (property.Metadata.IsPrimaryKey())
                    continue;

                if (pendingValues.TryGetValue(property.Metadata.Name, out var pending))
                {
                    property.CurrentValue = pending;
                    property.IsModified = true;
                }
                else
                {
                    property.CurrentValue = property.OriginalValue;
                    property.IsModified = false;
                }
            }
        }
    }

    /// <summary>
    /// Saves what is staged on this writer's context. <paramref name="firstRows"/> are the state rows this write
    /// added because it read that the context had none for their link.
    /// </summary>
    /// <remarks>
    /// Writers on other scopes (the hook sync queue, a sync, a pull, the single-repository refresh) add the first
    /// row of the same link at the same time, each after reading that there is none, and the unique index on
    /// (context, link) lets one insert through. The save that comes second fails as a whole and stores nothing.
    /// It is not an error of the write: the unsaved row is given up, the row the other writer stored is loaded, and
    /// <paramref name="stageAgainOnStoredRow"/> stages the write on it as on any existing row, so the groups this
    /// write did not probe keep what the other writer stored and the changed groups are compared with that row.
    /// Then the same context is saved again: everything else that was staged with the row (the link mirror, branch
    /// rows, changes the caller left pending) is still pending and goes with it. The method is not run again and the
    /// tracker is not cleared for this, because callers hold entities of this context that they write afterwards.
    /// A unique violation that is not a first row stored meanwhile surfaces.
    /// </remarks>
    private async Task SaveStagedAsync(
        IReadOnlyCollection<WorkspaceRepositoryContextState> firstRows,
        Action<WorkspaceRepositoryContextState> stageAgainOnStoredRow,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException ex) when (attempt < DbWriteConflict.MaxAttempts && DbWriteConflict.IsUniqueViolation(ex))
            {
                var unsaved = firstRows.Where(s => dbContext.Entry(s).State == EntityState.Added).ToList();
                if (unsaved.Count == 0)
                    throw;

                var contextId = unsaved[0].WorkspaceFeatureContextId;
                var linkIds = unsaved.Select(s => s.WorkspaceRepositoryId).ToList();
                var storedMeanwhile = await dbContext.WorkspaceRepositoryContextStates
                    .Where(s => s.WorkspaceFeatureContextId == contextId && linkIds.Contains(s.WorkspaceRepositoryId))
                    .ToListAsync(cancellationToken);
                if (storedMeanwhile.Count == 0)
                    throw;

                foreach (var stored in storedMeanwhile)
                {
                    var lost = unsaved.First(s => s.WorkspaceRepositoryId == stored.WorkspaceRepositoryId);
                    dbContext.Entry(lost).State = EntityState.Detached;
                    stageAgainOnStoredRow(stored);
                }
            }
        }
    }

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

    /// <summary>
    /// Writes the effective version columns of <paramref name="contextId"/> for each item: kind, version, the HEAD it was
    /// resolved for and the reason. Only existing context state rows are written; a repository without one keeps no
    /// stored decision (its readers then use the calculated GitVersion). The link is never touched. Returns how many
    /// rows were written.
    /// </summary>
    public async Task<int> WriteEffectiveVersionAsync(
        WorkspaceFeatureContextId contextId,
        IReadOnlyList<EffectiveVersionWrite> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return 0;

        var linkIds = items.Select(i => i.WorkspaceRepositoryId).Distinct().ToList();
        var states = await dbContext.WorkspaceRepositoryContextStates
            .Where(s => s.WorkspaceFeatureContextId == contextId.Value && linkIds.Contains(s.WorkspaceRepositoryId))
            .ToDictionaryAsync(s => s.WorkspaceRepositoryId, cancellationToken);

        var written = 0;
        foreach (var item in items)
        {
            if (!states.TryGetValue(item.WorkspaceRepositoryId, out var state))
                continue;

            var version = Blank(item.EffectiveVersion) ? null : item.EffectiveVersion!.Trim();
            var commit = Blank(item.ResolvedForCommitSha) ? null : item.ResolvedForCommitSha!.Trim();
            // An unchanged decision is not written again: recomputes run after every state write.
            if (state.EffectiveVersionKind == (int)item.Kind
                && state.EffectiveVersion == version
                && state.EffectiveVersionCommitSha == commit
                && state.EffectiveVersionReasonCode == item.ReasonCode
                && state.EffectiveVersionEvidenceKey == item.EvidenceKey)
                continue;

            state.EffectiveVersionKind = (int)item.Kind;
            state.EffectiveVersion = version;
            state.EffectiveVersionCommitSha = commit;
            state.EffectiveVersionReasonCode = item.ReasonCode;
            state.EffectiveVersionEvidenceKey = item.EvidenceKey;
            written++;
        }

        if (written > 0)
            await dbContext.SaveChangesAsync(cancellationToken);
        return written;
    }

    /// <summary>
    /// Stores the HEAD commit an operation has just read from the checkouts of <paramref name="contextId"/>, keyed by
    /// RepositoryId. Only existing context state rows are written: a repository without one has no recorded state to
    /// correct, and none is created from a commit alone. The link is never touched. Returns how many rows changed.
    /// </summary>
    public async Task<int> WriteHeadCommitsAsync(
        WorkspaceFeatureContextId contextId,
        IReadOnlyDictionary<int, string> headCommitByRepositoryId,
        CancellationToken cancellationToken = default)
    {
        if (headCommitByRepositoryId.Count == 0)
            return 0;

        var repositoryIds = headCommitByRepositoryId.Keys.ToList();
        var rows = await (
            from state in dbContext.WorkspaceRepositoryContextStates
            join link in dbContext.WorkspaceRepositories on state.WorkspaceRepositoryId equals link.WorkspaceRepositoryId
            where state.WorkspaceFeatureContextId == contextId.Value && repositoryIds.Contains(link.RepositoryId)
            select new { State = state, link.RepositoryId })
            .ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var row in rows)
        {
            var commit = headCommitByRepositoryId[row.RepositoryId].Trim();
            if (commit.Length == 0 || string.Equals(row.State.HeadCommit, commit, StringComparison.Ordinal))
                continue;

            row.State.HeadCommit = commit;
            changed++;
        }

        if (changed > 0)
            await dbContext.SaveChangesAsync(cancellationToken);
        return changed;
    }

    /// <summary>
    /// Records where a branch of <paramref name="contextId"/> started, after GrayMoon created it. Replaces an earlier row for
    /// the same (context, repository, branch): the branch was created again. Not used for Features, whose
    /// <see cref="WorkspaceFeatureRepository"/> row is the baseline.
    /// </summary>
    public async Task RecordBranchBaselineAsync(
        WorkspaceFeatureContextId contextId,
        int workspaceRepositoryId,
        string branchName,
        string parentBranchName,
        string baseCommitSha,
        string? resolvedStartRef,
        DateTime createdAtUtc,
        CancellationToken cancellationToken = default)
    {
        var parent = parentBranchName.Trim();
        if (parent.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
            parent = parent["origin/".Length..];

        var row = await dbContext.WorkspaceRepositoryBranchBaselines
            .FirstOrDefaultAsync(
                b => b.WorkspaceFeatureContextId == contextId.Value
                    && b.WorkspaceRepositoryId == workspaceRepositoryId
                    && b.BranchName == branchName,
                cancellationToken);
        if (row is null)
        {
            row = new WorkspaceRepositoryBranchBaseline
            {
                WorkspaceFeatureContextId = contextId.Value,
                WorkspaceRepositoryId = workspaceRepositoryId,
                BranchName = branchName,
            };
            dbContext.WorkspaceRepositoryBranchBaselines.Add(row);
        }

        row.ParentBranchName = parent;
        row.BaseCommitSha = baseCommitSha.Trim();
        row.ResolvedStartRef = Blank(resolvedStartRef) ? null : resolvedStartRef!.Trim();
        row.CreatedAtUtc = createdAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Deletes the recorded baseline of a local branch GrayMoon deleted. Returns the number of rows removed.</summary>
    public Task<int> DeleteBranchBaselineAsync(
        WorkspaceFeatureContextId contextId,
        int workspaceRepositoryId,
        string branchName,
        CancellationToken cancellationToken = default)
        => dbContext.WorkspaceRepositoryBranchBaselines
            .Where(b => b.WorkspaceFeatureContextId == contextId.Value
                && b.WorkspaceRepositoryId == workspaceRepositoryId
                && b.BranchName == branchName)
            .ExecuteDeleteAsync(cancellationToken);

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}

/// <summary>One effective version decision for <see cref="WorkspaceRepositoryStateWriter.WriteEffectiveVersionAsync"/>.</summary>
public sealed record EffectiveVersionWrite(
    int WorkspaceRepositoryId,
    EffectiveVersionKind Kind,
    string? EffectiveVersion,
    string? ResolvedForCommitSha,
    string ReasonCode,
    string? EvidenceKey = null);
