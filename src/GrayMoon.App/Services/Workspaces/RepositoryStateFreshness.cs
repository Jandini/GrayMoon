using System.Collections.Concurrent;
using System.Diagnostics;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// Remembers, per Feature context and repository, when the GrayMoon operation that last wrote each group of
/// checkout state began, so a hook snapshot that waited in the sync queue cannot put older state back.
/// </summary>
/// <remarks>
/// <para>
/// A hook snapshot is captured before the App receives it. An operation's result is captured after the
/// operation began. So a hook snapshot received before an operation began is older than that operation's
/// result, whatever order the two are written in. Both times are read on the App's own monotonic clock,
/// which keeps the comparison independent of the Worker's clock.
/// </para>
/// <para>
/// A snapshot received after the operation began is always applied: it may be newer. Only operations that
/// pass <see cref="RepositoryStateWriteOptions.OperationStartedAt"/> are recorded. The record lives in memory;
/// the sync queue it guards against does not survive a restart either.
/// </para>
/// </remarks>
public sealed class RepositoryStateFreshness
{
    private readonly ConcurrentDictionary<(int ContextId, int RepositoryId), Stamps> _stamps = new();

    /// <summary>The current time on the clock every stamp uses.</summary>
    public static long Now() => Stopwatch.GetTimestamp();

    /// <summary>Records that an operation begun at <paramref name="operationStartedAt"/> wrote the groups <paramref name="snapshot"/> probed.</summary>
    public void RecordOperationWrite(
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        RepositoryStateSnapshot snapshot,
        long operationStartedAt)
    {
        if (!snapshot.IdentityProbed && !snapshot.GitVersionProbed && !snapshot.CommitCountsProbed
            && !snapshot.UpstreamProbed && !snapshot.ProjectsProbed && !snapshot.BranchesProbed)
            return;

        _stamps.AddOrUpdate(
            (contextId.Value, repositoryId),
            _ => Merge(default, snapshot, operationStartedAt),
            (_, current) => Merge(current, snapshot, operationStartedAt));
    }

    /// <summary>
    /// Returns <paramref name="snapshot"/> without the groups an operation begun after <paramref name="receivedAt"/>
    /// has since written. The same instance comes back when nothing is superseded, and null when every group it
    /// probed is. Identity decides what the version, the counts, the upstream and the projects describe, so a
    /// superseded identity takes those groups with it.
    /// </summary>
    public RepositoryStateSnapshot? WithoutSupersededGroups(
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        RepositoryStateSnapshot snapshot,
        long receivedAt)
    {
        if (!_stamps.TryGetValue((contextId.Value, repositoryId), out var stamps))
            return snapshot;

        var checkoutSuperseded = snapshot.IdentityProbed && stamps.Identity > receivedAt;
        var dropIdentity = checkoutSuperseded;
        var dropGitVersion = snapshot.GitVersionProbed && (checkoutSuperseded || stamps.GitVersion > receivedAt);
        var dropCommitCounts = snapshot.CommitCountsProbed && (checkoutSuperseded || stamps.CommitCounts > receivedAt);
        var dropUpstream = snapshot.UpstreamProbed && (checkoutSuperseded || stamps.Upstream > receivedAt);
        var dropProjects = snapshot.ProjectsProbed && (checkoutSuperseded || stamps.Projects > receivedAt);
        var dropBranches = snapshot.BranchesProbed && stamps.Branches > receivedAt;

        if (!dropIdentity && !dropGitVersion && !dropCommitCounts && !dropUpstream && !dropProjects && !dropBranches)
            return snapshot;

        var trimmed = new RepositoryStateSnapshot
        {
            BranchName = dropIdentity ? null : snapshot.BranchName,
            CheckedOutTag = dropIdentity ? null : snapshot.CheckedOutTag,
            HeadCommit = dropIdentity ? null : snapshot.HeadCommit,
            GitVersion = dropGitVersion ? null : snapshot.GitVersion,
            DefaultBranchName = snapshot.DefaultBranchName,
            OutgoingCommits = dropCommitCounts ? null : snapshot.OutgoingCommits,
            IncomingCommits = dropCommitCounts ? null : snapshot.IncomingCommits,
            DefaultBranchBehind = dropCommitCounts ? null : snapshot.DefaultBranchBehind,
            DefaultBranchAhead = dropCommitCounts ? null : snapshot.DefaultBranchAhead,
            HasUpstream = dropUpstream ? null : snapshot.HasUpstream,
            LocalBranches = dropBranches ? null : snapshot.LocalBranches,
            RemoteBranches = dropBranches ? null : snapshot.RemoteBranches,
            Tags = dropBranches ? null : snapshot.Tags,
            Projects = dropProjects ? null : snapshot.Projects,
            ErrorMessage = snapshot.ErrorMessage,
            FatalSyncFailure = snapshot.FatalSyncFailure,
            NotCloned = snapshot.NotCloned,
            IdentityProbed = snapshot.IdentityProbed && !dropIdentity,
            GitVersionProbed = snapshot.GitVersionProbed && !dropGitVersion,
            CommitCountsProbed = snapshot.CommitCountsProbed && !dropCommitCounts,
            UpstreamProbed = snapshot.UpstreamProbed && !dropUpstream,
            BranchesProbed = snapshot.BranchesProbed && !dropBranches,
            ProjectsProbed = snapshot.ProjectsProbed && !dropProjects,
        };

        var anythingLeft = trimmed.IdentityProbed || trimmed.GitVersionProbed || trimmed.CommitCountsProbed
            || trimmed.UpstreamProbed || trimmed.BranchesProbed || trimmed.ProjectsProbed;
        return anythingLeft ? trimmed : null;
    }

    private static Stamps Merge(Stamps current, RepositoryStateSnapshot snapshot, long startedAt) => new(
        Identity: snapshot.IdentityProbed ? Math.Max(current.Identity, startedAt) : current.Identity,
        GitVersion: snapshot.GitVersionProbed ? Math.Max(current.GitVersion, startedAt) : current.GitVersion,
        CommitCounts: snapshot.CommitCountsProbed ? Math.Max(current.CommitCounts, startedAt) : current.CommitCounts,
        Upstream: snapshot.UpstreamProbed ? Math.Max(current.Upstream, startedAt) : current.Upstream,
        Projects: snapshot.ProjectsProbed ? Math.Max(current.Projects, startedAt) : current.Projects,
        Branches: snapshot.BranchesProbed ? Math.Max(current.Branches, startedAt) : current.Branches);

    /// <summary>Start time of the latest operation that wrote each group; zero when none has.</summary>
    private readonly record struct Stamps(
        long Identity,
        long GitVersion,
        long CommitCounts,
        long Upstream,
        long Projects,
        long Branches);
}
