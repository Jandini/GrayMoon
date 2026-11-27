using GrayMoon.App.Data;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>What a Git Changes status scan saw checked out in one repository of one context.</summary>
/// <param name="HeadCommit">Null for a branch without a commit.</param>
/// <param name="BranchName">Not compared when <paramref name="IsDetachedHead"/> is set.</param>
public sealed record RepositoryCheckoutObservation(
    int WorkspaceId,
    WorkspaceFeatureContextId ContextId,
    int RepositoryId,
    int WorkspaceRepositoryId,
    string? HeadCommit,
    string? BranchName,
    bool IsDetachedHead);

/// <summary>
/// Told what every Git Changes status scan saw checked out. See <see cref="RepositoryStateDriftMonitor"/>.
/// </summary>
public interface IRepositoryStateDriftMonitor
{
    /// <summary>Never blocks and never throws.</summary>
    void Observe(RepositoryCheckoutObservation observation);

    /// <summary>Removes everything remembered for a context that no longer exists.</summary>
    void ForgetContext(WorkspaceFeatureContextId contextId);
}

/// <summary>
/// Finds a checkout whose stored state missed a Git event, without waiting for another event. Git hooks are not a
/// journal: a hook that fails, runs while the App or the Worker is down, or is dropped on a reconnect is gone, and
/// the stored commit and branch then stay behind the checkout until the next Sync. Every Git Changes status scan,
/// including the periodic audit of each active Workspace, already reads the commit and the branch that are
/// checked out. This monitor compares that with the stored state.
/// </summary>
/// <remarks>
/// <para>
/// A status scan normally arrives before the hook that reports the same change, so a difference is expected for a
/// moment. The comparison is therefore made <see cref="Grace"/> after the first observation, against the latest
/// one. When the stored commit or branch still differs, one read of that repository is requested
/// (<see cref="IRepositoryStateReconciler"/>); nothing is written here. While the Workspace is under a structural
/// operation or the context is being changed, the comparison waits another <see cref="Grace"/>.
/// </para>
/// <para>
/// The recovery window for a lost event in an active Workspace is therefore at most the audit interval
/// (<c>GitChangesOptions.WatcherAuditIntervalMinutes</c>) plus the audit's own run time plus <see cref="Grace"/>; a
/// Workspace nobody looks at is audited when it becomes active. The same observation is requested again only after
/// <see cref="RetryAfter"/>, so a read that cannot repair the difference does not loop.
/// </para>
/// <para>
/// Not seen here: a branch or tag created or deleted without a HEAD move whose <c>reference-transaction</c> hook was
/// lost, and anything on the remote. Those are repaired by the next ref event, a fetch or a Sync.
/// </para>
/// </remarks>
public sealed class RepositoryStateDriftMonitor(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IRepositoryStateReconciler reconciler,
    IWorkspaceOperationLock operationLock,
    TimeProvider timeProvider,
    ILogger<RepositoryStateDriftMonitor> logger) : IRepositoryStateDriftMonitor, IDisposable
{
    /// <summary>How long the hook that reports a change may take to be applied before the difference counts as drift.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    /// <summary>The same observation asks for another read only after this long.</summary>
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    /// <summary>An observation found equal to the stored state is not compared again for this long.</summary>
    internal static readonly TimeSpan VerifiedFor = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Dictionary<(int ContextId, int RepositoryId), Entry> _entries = [];
    private readonly List<Task> _running = [];
    private readonly CancellationTokenSource _stopping = new();

    public void Observe(RepositoryCheckoutObservation observation)
    {
        if (_stopping.IsCancellationRequested)
            return;

        var key = (observation.ContextId.Value, observation.RepositoryId);
        var now = timeProvider.GetUtcNow();
        Task? check = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
                _entries[key] = entry = new Entry();

            // The most common case: another scan of a checkout that was already compared and found equal.
            if (entry.Verified is { } verified && SameCheckout(verified, observation) && now - entry.VerifiedAt < VerifiedFor)
                return;

            entry.Latest = observation;
            if (entry.Checking)
                return;

            entry.Checking = true;
            check = Task.Run(() => CheckAfterGraceAsync(key, entry));
            _running.Add(check);
        }

        _ = check.ContinueWith(
            t =>
            {
                lock (_gate)
                    _running.Remove(t);
            },
            TaskScheduler.Default);
    }

    public void ForgetContext(WorkspaceFeatureContextId contextId)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(k => k.ContextId == contextId.Value).ToList())
                _entries.Remove(key);
        }
    }

    /// <summary>Completes when no comparison is waiting or running. Used by tests.</summary>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] snapshot;
            lock (_gate)
                snapshot = [.. _running];
            if (snapshot.Length == 0)
                return;

            await Task.WhenAll(snapshot);
            await Task.Yield();
        }
    }

    /// <summary>True while any comparison is waiting or running. Used by tests.</summary>
    internal bool HasPendingChecks
    {
        get
        {
            lock (_gate)
                return _running.Count > 0;
        }
    }

    /// <summary>True while a comparison of this repository is waiting for its grace period. Used by tests.</summary>
    internal bool IsWaiting(WorkspaceFeatureContextId contextId, int repositoryId)
    {
        lock (_gate)
            return _entries.TryGetValue((contextId.Value, repositoryId), out var entry) && entry.Checking;
    }

    private async Task CheckAfterGraceAsync((int ContextId, int RepositoryId) key, Entry entry)
    {
        try
        {
            while (true)
            {
                await Task.Delay(Grace, timeProvider, _stopping.Token);

                RepositoryCheckoutObservation observation;
                lock (_gate)
                {
                    // The context was deleted while this waited.
                    if (!_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry) || entry.Latest is null)
                    {
                        entry.Checking = false;
                        return;
                    }

                    observation = entry.Latest;
                }

                // An operation is changing this checkout and reports its own result: compare when it has ended.
                if (operationLock.IsWorkspaceStructurallyBusy(observation.WorkspaceId) || operationLock.IsContextBusy(observation.ContextId))
                    continue;

                var stored = await ReadStoredAsync(observation, _stopping.Token);
                var now = timeProvider.GetUtcNow();
                var request = false;
                lock (_gate)
                {
                    // A newer scan arrived during the read: compare that one, after another grace period.
                    if (!ReferenceEquals(entry.Latest, observation))
                        continue;

                    entry.Checking = false;
                    if (stored is null)
                    {
                        // No state row: the context or the link is gone, or the repository was never synced.
                        _entries.Remove(key);
                        return;
                    }

                    if (Matches(observation, stored.Value.HeadCommit, stored.Value.BranchName))
                    {
                        entry.Verified = observation;
                        entry.VerifiedAt = now;
                        entry.Requested = null;
                        return;
                    }

                    if (entry.Requested is { } requested && SameCheckout(requested, observation) && now - entry.RequestedAt < RetryAfter)
                        return;

                    entry.Requested = observation;
                    entry.RequestedAt = now;
                    request = true;
                }

                if (request)
                {
                    logger.LogInformation(
                        "Stored repository state is behind the checkout: workspace={WorkspaceId}, context={ContextId}, repo={RepositoryId}, storedCommit={StoredCommit}, checkedOutCommit={CheckedOutCommit}, storedBranch={StoredBranch}, checkedOutBranch={CheckedOutBranch}",
                        observation.WorkspaceId, observation.ContextId.Value, observation.RepositoryId,
                        stored!.Value.HeadCommit, observation.HeadCommit, stored.Value.BranchName, observation.BranchName);
                    reconciler.Request(
                        observation.WorkspaceId, observation.ContextId, observation.RepositoryId, "the checkout differs from the stored state (a Git event was missed)");
                }

                return;
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // The next scan observes again.
            lock (_gate)
                entry.Checking = false;
            logger.LogWarning(ex, "Repository state drift check failed: context={ContextId}, repo={RepositoryId}", key.ContextId, key.RepositoryId);
        }
    }

    private async Task<(string? HeadCommit, string? BranchName)?> ReadStoredAsync(RepositoryCheckoutObservation observation, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == observation.ContextId.Value && s.WorkspaceRepositoryId == observation.WorkspaceRepositoryId)
            .Select(s => new { s.HeadCommit, s.BranchName })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : (row.HeadCommit, row.BranchName);
    }

    /// <summary>
    /// Whether the stored commit and branch describe the observed checkout. A value that is not known on either
    /// side is not a contradiction: only two known, different values are.
    /// </summary>
    internal static bool Matches(RepositoryCheckoutObservation observation, string? storedHead, string? storedBranch)
    {
        if (!string.IsNullOrWhiteSpace(observation.HeadCommit) && !string.IsNullOrWhiteSpace(storedHead)
            && !string.Equals(observation.HeadCommit.Trim(), storedHead.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        return observation.IsDetachedHead
               || string.IsNullOrWhiteSpace(observation.BranchName)
               || string.IsNullOrWhiteSpace(storedBranch)
               || string.Equals(observation.BranchName.Trim(), storedBranch.Trim(), StringComparison.Ordinal);
    }

    private static bool SameCheckout(RepositoryCheckoutObservation a, RepositoryCheckoutObservation b)
        => string.Equals(a.HeadCommit, b.HeadCommit, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.BranchName, b.BranchName, StringComparison.Ordinal)
           && a.IsDetachedHead == b.IsDetachedHead;

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private sealed class Entry
    {
        public RepositoryCheckoutObservation? Latest;
        public bool Checking;
        public RepositoryCheckoutObservation? Verified;
        public DateTimeOffset VerifiedAt;
        public RepositoryCheckoutObservation? Requested;
        public DateTimeOffset RequestedAt;
    }
}
