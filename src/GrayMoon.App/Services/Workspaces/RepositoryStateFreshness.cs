using System.Collections.Concurrent;
using System.Diagnostics;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>The groups of a <see cref="RepositoryStateSnapshot"/>, one flag per probe marker.</summary>
[Flags]
public enum RepositoryFactGroups
{
    None = 0,

    /// <summary>Branch, tag and HEAD commit.</summary>
    Identity = 1 << 0,
    GitVersion = 1 << 1,
    CommitCounts = 1 << 2,
    Upstream = 1 << 3,

    /// <summary>Local and remote branch lists and tags. They belong to the repository: every context of a Workspace shares them.</summary>
    Branches = 1 << 4,
    Projects = 1 << 5,

    /// <summary>The groups that describe one checkout, and that a newer identity takes with it.</summary>
    Checkout = Identity | GitVersion | CommitCounts | Upstream | Projects,
    All = Checkout | Branches,
}

/// <summary>What <see cref="RepositoryStateFreshness.Evaluate"/> decided about a snapshot that waited before it could be applied.</summary>
/// <param name="Snapshot">The snapshot to apply: the one given, a copy without the superseded groups, or null when nothing is left.</param>
/// <param name="Superseded">Groups left out because newer evidence was already stored.</param>
/// <param name="Ambiguous">
/// Groups that are applied although their order against the stored evidence is not known: the two captures
/// overlapped on the Worker. The caller reads the repository again instead of trusting either.
/// </param>
/// <param name="Duplicate">The same capture was already applied; nothing is applied again.</param>
public readonly record struct RepositoryStateFreshnessVerdict(
    RepositoryStateSnapshot? Snapshot,
    RepositoryFactGroups Superseded,
    RepositoryFactGroups Ambiguous,
    bool Duplicate);

/// <summary>
/// Remembers, per context and repository, the evidence behind each group of stored checkout state, so that a
/// report that is older than what is stored cannot put older state back, whatever order reports arrive in.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of evidence are kept. <b>Operations:</b> when the GrayMoon operation that last wrote a group began, on
/// the App's own monotonic clock. A hook snapshot is captured before the App receives it, and an operation's
/// result is captured after the operation began, so a hook snapshot received before an operation began is older
/// than that operation's result. <b>Captures:</b> the Worker's capture interval
/// (<see cref="RepositoryStateSnapshot.CaptureStart"/> to <see cref="RepositoryStateSnapshot.CaptureEnd"/>) of the
/// last snapshot applied to a group. Between two snapshots of one Worker session that decides the order even when
/// the older one arrives later: a snapshot whose capture ended before the stored capture began is dropped, one
/// captured again is a duplicate, and two whose captures overlap cannot be ordered and are reported as ambiguous.
/// </para>
/// <para>
/// Branch lists and tags belong to the repository, not to a checkout: every context of a Workspace (its main
/// checkout and each Feature worktree) writes the same rows. Their evidence is therefore shared by the contexts of
/// a Workspace, so a list read through one worktree is not overwritten by an older list read through another.
/// </para>
/// <para>
/// A snapshot without capture evidence, or from another Worker session, is ordered by arrival as before: it is
/// applied unless an operation that began after it arrived has written the group. The record lives in memory; the
/// sync queue it guards does not survive a restart either. <see cref="ForgetContext"/> removes a context's entries
/// when the context is deleted.
/// </para>
/// </remarks>
public sealed class RepositoryStateFreshness
{
    private readonly ConcurrentDictionary<(int Scope, int RepositoryId), Evidence> _evidence = new();

    /// <summary>The current time on the clock every operation stamp uses.</summary>
    public static long Now() => Stopwatch.GetTimestamp();

    /// <summary>Records that an operation begun at <paramref name="operationStartedAt"/> wrote the groups <paramref name="snapshot"/> probed.</summary>
    /// <param name="workspaceId">The context's Workspace. When given, the branch lists are recorded for every context of it.</param>
    public void RecordOperationWrite(
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        RepositoryStateSnapshot snapshot,
        long operationStartedAt,
        int? workspaceId = null)
    {
        var probed = ProbedGroups(snapshot);
        if (probed == RepositoryFactGroups.None)
            return;

        Update(contextId.Value, repositoryId, probed & RepositoryFactGroups.Checkout, e => e.WithOperation(probed, operationStartedAt));
        Update(SharedScope(contextId, workspaceId), repositoryId, probed & RepositoryFactGroups.Branches, e => e.WithOperation(probed, operationStartedAt));
    }

    /// <summary>
    /// Records the capture evidence of a snapshot that was applied, for the groups it probed. A snapshot without
    /// capture evidence records nothing: the next one is then ordered by arrival.
    /// </summary>
    public void RecordApplied(
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        RepositoryStateSnapshot snapshot,
        int? workspaceId = null)
    {
        if (CaptureOf(snapshot) is not { } capture)
            return;

        var probed = ProbedGroups(snapshot);
        Update(contextId.Value, repositoryId, probed & RepositoryFactGroups.Checkout, e => e.WithCapture(probed, capture));
        Update(SharedScope(contextId, workspaceId), repositoryId, probed & RepositoryFactGroups.Branches, e => e.WithCapture(probed, capture));
    }

    /// <summary>
    /// Decides what of <paramref name="snapshot"/> may still be applied. Identity decides what the version, the
    /// counts, the upstream and the projects describe, so a superseded identity takes those groups with it.
    /// </summary>
    /// <param name="receivedAt"><see cref="Now"/> read when the snapshot reached the App.</param>
    /// <param name="workspaceId">The context's Workspace, so branch lists are judged against every context of it.</param>
    public RepositoryStateFreshnessVerdict Evaluate(
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        RepositoryStateSnapshot snapshot,
        long receivedAt,
        int? workspaceId = null)
    {
        var probed = ProbedGroups(snapshot);
        _evidence.TryGetValue((contextId.Value, repositoryId), out var own);
        _evidence.TryGetValue((SharedScope(contextId, workspaceId), repositoryId), out var shared);
        if (own is null && shared is null)
            return new RepositoryStateFreshnessVerdict(snapshot, RepositoryFactGroups.None, RepositoryFactGroups.None, false);

        var capture = CaptureOf(snapshot);
        var superseded = RepositoryFactGroups.None;
        var ambiguous = RepositoryFactGroups.None;
        var duplicates = RepositoryFactGroups.None;
        foreach (var group in EachGroup(probed))
        {
            var stored = group == RepositoryFactGroups.Branches ? shared : own;
            if (stored is null)
                continue;

            if (stored.OperationStartedAt(group) > receivedAt)
            {
                superseded |= group;
                continue;
            }

            if (capture is not { } incoming || stored.Capture(group) is not { } applied || applied.Session != incoming.Session)
                continue;

            if (applied.Start == incoming.Start && applied.End == incoming.End)
                duplicates |= group;
            else if (applied.Start > incoming.End)
                superseded |= group;
            else if (incoming.Start <= applied.End)
                ambiguous |= group;
        }

        // Every probed group was applied from this very capture: a redelivery.
        if (duplicates != RepositoryFactGroups.None && duplicates == probed)
            return new RepositoryStateFreshnessVerdict(null, RepositoryFactGroups.None, RepositoryFactGroups.None, true);
        superseded |= duplicates;

        if ((superseded & RepositoryFactGroups.Identity) != 0)
            superseded |= probed & RepositoryFactGroups.Checkout;
        ambiguous &= ~superseded;

        if (superseded == RepositoryFactGroups.None)
            return new RepositoryStateFreshnessVerdict(snapshot, RepositoryFactGroups.None, ambiguous, false);

        var remaining = probed & ~superseded;
        return new RepositoryStateFreshnessVerdict(
            remaining == RepositoryFactGroups.None ? null : Without(snapshot, superseded),
            superseded,
            ambiguous,
            false);
    }

    /// <summary>
    /// Returns <paramref name="snapshot"/> without the groups that newer evidence has since written. The same instance
    /// comes back when nothing is superseded, and null when every group it probed is. See <see cref="Evaluate"/>.
    /// </summary>
    public RepositoryStateSnapshot? WithoutSupersededGroups(
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        RepositoryStateSnapshot snapshot,
        long receivedAt)
        => Evaluate(contextId, repositoryId, snapshot, receivedAt).Snapshot;

    /// <summary>Removes everything remembered for a context that no longer exists.</summary>
    public void ForgetContext(WorkspaceFeatureContextId contextId)
    {
        foreach (var key in _evidence.Keys)
        {
            if (key.Scope == contextId.Value)
                _evidence.TryRemove(key, out _);
        }
    }

    /// <summary>Removes the branch list evidence the contexts of a Workspace shared, when the Workspace is removed.</summary>
    public void ForgetWorkspace(int workspaceId)
    {
        foreach (var key in _evidence.Keys)
        {
            if (key.Scope == -workspaceId)
                _evidence.TryRemove(key, out _);
        }
    }

    /// <summary>The groups a snapshot probed.</summary>
    public static RepositoryFactGroups ProbedGroups(RepositoryStateSnapshot snapshot)
        => (snapshot.IdentityProbed ? RepositoryFactGroups.Identity : 0)
           | (snapshot.GitVersionProbed ? RepositoryFactGroups.GitVersion : 0)
           | (snapshot.CommitCountsProbed ? RepositoryFactGroups.CommitCounts : 0)
           | (snapshot.UpstreamProbed ? RepositoryFactGroups.Upstream : 0)
           | (snapshot.BranchesProbed ? RepositoryFactGroups.Branches : 0)
           | (snapshot.ProjectsProbed ? RepositoryFactGroups.Projects : 0);

    private void Update(int scope, int repositoryId, RepositoryFactGroups groups, Func<Evidence, Evidence> change)
    {
        if (groups == RepositoryFactGroups.None)
            return;

        _evidence.AddOrUpdate((scope, repositoryId), _ => change(Evidence.Empty), (_, current) => change(current));
    }

    /// <summary>
    /// The key of the evidence the contexts of one Workspace share. Negative, so it never meets a context id; without
    /// a Workspace id the context keeps its own, as before the lists were shared.
    /// </summary>
    private static int SharedScope(WorkspaceFeatureContextId contextId, int? workspaceId)
        => workspaceId is { } id ? -id : contextId.Value;

    private static CaptureStamp? CaptureOf(RepositoryStateSnapshot snapshot)
        => !string.IsNullOrEmpty(snapshot.CaptureSession) && snapshot.CaptureStart is { } start && snapshot.CaptureEnd is { } end && end >= start
            ? new CaptureStamp(snapshot.CaptureSession, start, end)
            : null;

    private static IEnumerable<RepositoryFactGroups> EachGroup(RepositoryFactGroups groups)
    {
        foreach (var group in Evidence.Groups)
        {
            if ((groups & group) != 0)
                yield return group;
        }
    }

    private static RepositoryStateSnapshot Without(RepositoryStateSnapshot snapshot, RepositoryFactGroups dropped)
    {
        bool Dropped(RepositoryFactGroups group) => (dropped & group) != 0;
        var dropIdentity = Dropped(RepositoryFactGroups.Identity);
        var dropGitVersion = Dropped(RepositoryFactGroups.GitVersion);
        var dropCommitCounts = Dropped(RepositoryFactGroups.CommitCounts);
        var dropUpstream = Dropped(RepositoryFactGroups.Upstream);
        var dropProjects = Dropped(RepositoryFactGroups.Projects);
        var dropBranches = Dropped(RepositoryFactGroups.Branches);
        return new RepositoryStateSnapshot
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
            CaptureSession = snapshot.CaptureSession,
            CaptureStart = snapshot.CaptureStart,
            CaptureEnd = snapshot.CaptureEnd,
        };
    }

    /// <summary>The capture interval of one snapshot on one Worker session.</summary>
    private readonly record struct CaptureStamp(string Session, long Start, long End);

    /// <summary>Per group: when the latest operation that wrote it began (zero when none), and the capture of the latest snapshot applied to it.</summary>
    private sealed class Evidence
    {
        public static readonly RepositoryFactGroups[] Groups =
        [
            RepositoryFactGroups.Identity,
            RepositoryFactGroups.GitVersion,
            RepositoryFactGroups.CommitCounts,
            RepositoryFactGroups.Upstream,
            RepositoryFactGroups.Branches,
            RepositoryFactGroups.Projects,
        ];

        public static readonly Evidence Empty = new(new long[Groups.Length], new CaptureStamp?[Groups.Length]);

        private readonly long[] _operations;
        private readonly CaptureStamp?[] _captures;

        private Evidence(long[] operations, CaptureStamp?[] captures)
        {
            _operations = operations;
            _captures = captures;
        }

        public long OperationStartedAt(RepositoryFactGroups group) => _operations[IndexOf(group)];

        public CaptureStamp? Capture(RepositoryFactGroups group) => _captures[IndexOf(group)];

        public Evidence WithOperation(RepositoryFactGroups groups, long startedAt)
        {
            var operations = (long[])_operations.Clone();
            for (var i = 0; i < Groups.Length; i++)
            {
                if ((groups & Groups[i]) != 0)
                    operations[i] = Math.Max(operations[i], startedAt);
            }

            return new Evidence(operations, _captures);
        }

        public Evidence WithCapture(RepositoryFactGroups groups, CaptureStamp capture)
        {
            var captures = (CaptureStamp?[])_captures.Clone();
            for (var i = 0; i < Groups.Length; i++)
            {
                if ((groups & Groups[i]) == 0)
                    continue;

                // Another session starts a new order; within a session the capture that ended last stays.
                if (captures[i] is not { } current || current.Session != capture.Session || capture.End >= current.End)
                    captures[i] = capture;
            }

            return new Evidence(_operations, captures);
        }

        private static int IndexOf(RepositoryFactGroups group) => Array.IndexOf(Groups, group);
    }
}
