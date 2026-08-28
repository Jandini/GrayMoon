using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services;

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
/// The single writer of the denormalized badge columns on <see cref="WorkspaceRepositoryLink"/>.
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
    ILogger<WorkspaceRepositoryStateWriter> logger)
{
    /// <summary>Applies <paramref name="snapshot"/> to the workspace-repository link. Returns false when the link does not exist.</summary>
    public async Task<bool> ApplyAsync(
        int workspaceId,
        int repositoryId,
        RepositoryStateSnapshot snapshot,
        RepositoryStateWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new RepositoryStateWriteOptions();

        var wr = await dbContext.WorkspaceRepositories
            .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.RepositoryId == repositoryId, cancellationToken);
        if (wr == null)
        {
            logger.LogWarning("State write skipped: workspace {WorkspaceId} repository {RepositoryId} not linked", workspaceId, repositoryId);
            return false;
        }

        var previousBranch = wr.BranchName;

        ApplyIdentity(wr, snapshot);

        if (snapshot.GitVersionProbed)
            wr.GitVersion = Blank(snapshot.GitVersion) ? null : snapshot.GitVersion;

        // A repository pinned to a tag has no branch to count against, so those columns stay cleared
        // regardless of what the snapshot reports.
        var onTag = !string.IsNullOrWhiteSpace(wr.CheckedOutTag);
        if (!onTag)
        {
            // Only a probed group is written. An unprobed group keeps the value the grid is already showing
            // until a flow that did probe replaces it - blanking it to "-" mid-operation reads as a bug even
            // though the counts are about to arrive.
            if (snapshot.CommitCountsProbed)
            {
                wr.OutgoingCommits = snapshot.OutgoingCommits;
                wr.IncomingCommits = snapshot.IncomingCommits;
                // Ahead/behind vs default is a separate comparison. A pair of nulls means that comparison
                // did not finish (or was never run), not "no divergence" - writing them would flash "-"
                // until a later snapshot arrives. 0 is a real answer and still replaces.
                if (snapshot.DefaultBranchBehind.HasValue || snapshot.DefaultBranchAhead.HasValue)
                {
                    wr.DefaultBranchBehindCommits = snapshot.DefaultBranchBehind;
                    wr.DefaultBranchAheadCommits = snapshot.DefaultBranchAhead;
                }
            }

            if (snapshot.UpstreamProbed)
                wr.BranchHasUpstream = snapshot.HasUpstream;
        }

        // Written whenever the agent knows it, independently of any marker: every flow that can report
        // a default branch should refresh it, otherwise a stale value keeps a synced repository looking
        // like it is still off-default.
        if (!Blank(snapshot.DefaultBranchName))
            wr.DefaultBranchName = snapshot.DefaultBranchName;

        ApplySyncStatus(wr, snapshot, options);

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
            await ApplyProjectsAsync(workspaceId, repositoryId, wr, snapshot, cancellationToken);

        if (options.ReconcilePullRequest)
            await ReconcilePullRequestAsync(workspaceId, repositoryId, wr, previousBranch, cancellationToken);

        return true;
    }

    private static void ApplyIdentity(WorkspaceRepositoryLink wr, RepositoryStateSnapshot snapshot)
    {
        if (!snapshot.IdentityProbed)
            return;

        if (!Blank(snapshot.CheckedOutTag))
        {
            // Detached HEAD on a tag: write actions are blocked and there is no branch, so clear the
            // branch-scoped fields rather than leaving the previous branch's badges rendered.
            wr.CheckedOutTag = snapshot.CheckedOutTag;
            wr.BranchName = null;
            wr.BranchHasUpstream = null;
            wr.OutgoingCommits = null;
            wr.IncomingCommits = null;
            wr.DefaultBranchBehindCommits = null;
            wr.DefaultBranchAheadCommits = null;
            return;
        }

        wr.CheckedOutTag = null;
        wr.HasNewerTag = null;
        wr.BranchName = Blank(snapshot.BranchName) || snapshot.BranchName == "-" ? null : snapshot.BranchName;
    }

    private static void ApplySyncStatus(WorkspaceRepositoryLink wr, RepositoryStateSnapshot snapshot, RepositoryStateWriteOptions options)
    {
        switch (options.SyncStatus)
        {
            case SyncStatusWrite.InSync:
                wr.SyncStatus = RepoSyncStatus.InSync;
                return;
            case SyncStatusWrite.Error:
                wr.SyncStatus = RepoSyncStatus.Error;
                return;
            case SyncStatusWrite.Leave:
                return;
        }

        if (options.ErrorMessageForcesInSync && !Blank(snapshot.ErrorMessage))
        {
            wr.SyncStatus = RepoSyncStatus.InSync;
            return;
        }

        var hasValidVersion = !Blank(wr.GitVersion) && (!Blank(wr.BranchName) || !Blank(wr.CheckedOutTag));
        var hasDefaultBranch = !Blank(wr.DefaultBranchName);
        wr.SyncStatus = !hasValidVersion
            ? RepoSyncStatus.Error
            : hasDefaultBranch ? RepoSyncStatus.InSync : RepoSyncStatus.NeedsSync;
    }

    private async Task ApplyProjectsAsync(
        int workspaceId,
        int repositoryId,
        WorkspaceRepositoryLink wr,
        RepositoryStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var projects = ToSyncProjects(snapshot.Projects);

        // An empty list from a probed scan is meaningful: the branch now checked out genuinely has no
        // projects, so the previous branch's projects (and, by cascade, their dependency edges) go away.
        await workspaceProjectRepository.MergeWorkspaceProjectsAsync(workspaceId, repositoryId, projects, cancellationToken);

        wr.Projects = projects.Count;
        wr.RepositoryType = ComputeRepositoryType(projects);
        await dbContext.SaveChangesAsync(cancellationToken);

        await workspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync(
            workspaceId,
            [(repositoryId, (IReadOnlyList<SyncProjectInfo>?)projects)],
            persistDependencyLevel: false,
            cancellationToken);
    }

    private async Task ReconcilePullRequestAsync(
        int workspaceId,
        int repositoryId,
        WorkspaceRepositoryLink wr,
        string? previousBranch,
        CancellationToken cancellationToken)
    {
        // The cache is keyed by (repo, branch); leaving the old branch's entry behind would let a later
        // checkout back onto it serve a PR state captured before the branch was merged or closed.
        var branchChanged = !string.Equals(previousBranch, wr.BranchName, StringComparison.Ordinal);
        if (branchChanged)
            pullRequestService.EvictCacheForRepository(repositoryId);

        var branch = wr.BranchName;
        var isOnDefault = !Blank(branch) && !Blank(wr.DefaultBranchName)
            && string.Equals(branch, wr.DefaultBranchName, StringComparison.OrdinalIgnoreCase);

        if (Blank(branch) || isOnDefault)
        {
            // No branch, or the default branch - GitHub cannot have a pull request from a branch to
            // itself, so clear the row directly instead of making a call that could fail and leave the
            // previous branch's merged badge on screen.
            await pullRequestService.ClearPullRequestAsync(workspaceId, repositoryId, cancellationToken);
            return;
        }

        // Only force a fresh GitHub call when the branch actually changed (cache was just evicted for it).
        // Otherwise every hook sync (e.g. a plain commit on the same branch) would bypass the 60s cache and
        // hit the API even though the PR state for this branch could not have changed.
        await pullRequestService.RefreshPullRequestsAsync(workspaceId, [repositoryId], force: branchChanged, cancellationToken);
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
