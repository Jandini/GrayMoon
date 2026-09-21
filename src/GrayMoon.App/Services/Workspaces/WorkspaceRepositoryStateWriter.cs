using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>How a write path wants <see cref="WorkspaceRepositoryLink.SyncStatus"/> handled.</summary>
public enum SyncStatusWrite
{
    /// <summary>Leave the persisted status alone (branch-only operations such as delete-branch or update-from-default).</summary>
    Leave,

    /// <summary>Derive it from the snapshot: Error without a usable version, NeedsSync without a known default branch, otherwise InSync.</summary>
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

        var previousBranch = state.BranchName;
        var isSpecialWorkspace = context.Kind == WorkspaceFeatureContextKind.Workspace;

        ApplyIdentity(state, snapshot);
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
            if (isSpecialWorkspace)
                wr.GitVersion = state.GitVersion;
        }

        var onTag = !string.IsNullOrWhiteSpace(state.CheckedOutTag);
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

        ApplySyncStatus(state, snapshot, options, wr.DefaultBranchName);
        if (isSpecialWorkspace)
            wr.SyncStatus = state.SyncStatus;

        await dbContext.SaveChangesAsync(cancellationToken);

        if (snapshot.BranchesProbed)
        {
            await branchWriter.PersistAsync(
                wr.WorkspaceRepositoryId,
                snapshot.LocalBranches,
                snapshot.RemoteBranches,
                snapshot.DefaultBranchName ?? wr.DefaultBranchName,
                snapshot.Tags,
                snapshot.CheckedOutTag,
                cancellationToken);
        }

        if (snapshot.ProjectsProbed)
            await ApplyProjectsAsync(contextId, workspaceId, repositoryId, wr, state, isSpecialWorkspace, snapshot, cancellationToken);

        if (options.ReconcilePullRequest)
            await ReconcilePullRequestAsync(contextId, workspaceId, repositoryId, wr, state, previousBranch, isSpecialWorkspace, cancellationToken);

        return true;
    }

    private static void ApplyIdentity(WorkspaceRepositoryContextState state, RepositoryStateSnapshot snapshot)
    {
        if (!snapshot.IdentityProbed)
            return;

        if (!Blank(snapshot.CheckedOutTag))
        {
            state.CheckedOutTag = snapshot.CheckedOutTag;
            state.BranchName = null;
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
        string? defaultBranchName)
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

        var hasValidVersion = !Blank(state.GitVersion) && (!Blank(state.BranchName) || !Blank(state.CheckedOutTag));
        var hasDefaultBranch = !Blank(defaultBranchName);
        state.SyncStatus = !hasValidVersion
            ? RepoSyncStatus.Error
            : hasDefaultBranch ? RepoSyncStatus.InSync : RepoSyncStatus.NeedsSync;
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

        // An empty list from a probed scan is meaningful: the branch now checked out genuinely has no
        // projects, so the previous branch's projects (and, by cascade, their dependency edges) go away.
        await workspaceProjectRepository.MergeWorkspaceProjectsAsync(
            workspaceId, repositoryId, projects, contextId.Value, cancellationToken);

        state.Projects = projects.Count;
        state.RepositoryType = ComputeRepositoryType(projects);
        if (isSpecialWorkspace)
        {
            wr.Projects = state.Projects;
            wr.RepositoryType = state.RepositoryType;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await workspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync(
            workspaceId,
            [(repositoryId, (IReadOnlyList<SyncProjectInfo>?)projects)],
            contextId.Value,
            persistDependencyLevel: false,
            cancellationToken);
    }

    private async Task ReconcilePullRequestAsync(
        WorkspaceFeatureContextId contextId,
        int workspaceId,
        int repositoryId,
        WorkspaceRepositoryLink wr,
        WorkspaceRepositoryContextState state,
        string? previousBranch,
        bool isSpecialWorkspace,
        CancellationToken cancellationToken)
    {
        var branchChanged = !string.Equals(previousBranch, state.BranchName, StringComparison.Ordinal);
        if (branchChanged)
            pullRequestService.EvictCacheForRepository(repositoryId);

        // WorkspacePullRequestService/WorkspaceRepositoryPullRequest are still keyed by WorkspaceRepositoryId
        // alone (one PR row per repository, not per context - see design doc §17/§37.3). Mutating
        // wr.BranchName or the legacy PR row from a Feature context write would silently overwrite the
        // special Workspace's own branch/PR display with the Feature's, corrupting the shared checkout state
        // every other page reads. Until that service is migrated to accept an explicit context/branch, only
        // the special Workspace context is allowed to touch the shared link + legacy PR row here; a Feature's
        // own PR projection is intentionally left unpopulated rather than risking that corruption.
        if (!isSpecialWorkspace)
            return;

        wr.BranchName = state.BranchName;

        var branch = state.BranchName;
        var isOnDefault = !Blank(branch) && !Blank(wr.DefaultBranchName)
            && string.Equals(branch, wr.DefaultBranchName, StringComparison.OrdinalIgnoreCase);

        if (Blank(branch) || isOnDefault)
        {
            await pullRequestService.ClearPullRequestAsync(workspaceId, repositoryId, cancellationToken);
            await ClearContextPullRequestAsync(contextId, wr.WorkspaceRepositoryId, cancellationToken);
            return;
        }

        await pullRequestService.RefreshPullRequestsAsync(workspaceId, [repositoryId], force: branchChanged, cancellationToken);
        await MirrorPullRequestToContextAsync(contextId, wr.WorkspaceRepositoryId, cancellationToken);
    }

    private async Task ClearContextPullRequestAsync(
        WorkspaceFeatureContextId contextId,
        int workspaceRepositoryId,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.WorkspaceRepositoryContextPullRequests
            .FirstOrDefaultAsync(
                pr => pr.WorkspaceFeatureContextId == contextId.Value && pr.WorkspaceRepositoryId == workspaceRepositoryId,
                cancellationToken);
        if (row is not null)
        {
            dbContext.WorkspaceRepositoryContextPullRequests.Remove(row);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task MirrorPullRequestToContextAsync(
        WorkspaceFeatureContextId contextId,
        int workspaceRepositoryId,
        CancellationToken cancellationToken)
    {
        var legacy = await dbContext.WorkspaceRepositoryPullRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(pr => pr.WorkspaceRepositoryId == workspaceRepositoryId, cancellationToken);

        var row = await dbContext.WorkspaceRepositoryContextPullRequests
            .FirstOrDefaultAsync(
                pr => pr.WorkspaceFeatureContextId == contextId.Value && pr.WorkspaceRepositoryId == workspaceRepositoryId,
                cancellationToken);

        if (legacy is null)
        {
            if (row is not null)
            {
                dbContext.WorkspaceRepositoryContextPullRequests.Remove(row);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        if (row is null)
        {
            row = new WorkspaceRepositoryContextPullRequest
            {
                WorkspaceFeatureContextId = contextId.Value,
                WorkspaceRepositoryId = workspaceRepositoryId
            };
            dbContext.WorkspaceRepositoryContextPullRequests.Add(row);
        }

        row.PullRequestNumber = legacy.PullRequestNumber;
        row.State = legacy.State;
        row.Mergeable = legacy.Mergeable;
        row.MergeableState = legacy.MergeableState;
        row.HtmlUrl = legacy.HtmlUrl;
        row.MergedAt = legacy.MergedAt;
        row.ChangedFiles = legacy.ChangedFiles;
        row.LastCheckedAt = legacy.LastCheckedAt;
        await dbContext.SaveChangesAsync(cancellationToken);
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

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
