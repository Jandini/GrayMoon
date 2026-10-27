using System.Collections.Concurrent;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>What a combined run needs to push after the user approves a normal push.</summary>
public sealed record PendingNormalPush(
    string ApprovalId,
    string RunId,
    int WorkspaceId,
    WorkspaceFeatureContextId ContextId,
    int? MaxLevel,
    bool RestorePackages,
    IReadOnlySet<int> RepositoryIds,
    IReadOnlySet<int> SyncedRepoIds,
    DateTime CreatedUtc);

/// <summary>
/// In-memory, process-wide record of the one normal-push approval a combined run is waiting for, per Workspace context.
/// An approval is single use, bound to its Workspace and context, and expires; it holds ids only, never services or
/// credentials. A new request for the same context replaces the older one.
/// </summary>
public sealed class UpdateAndPushApprovalStore(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(int WorkspaceId, int ContextId), PendingNormalPush> _pending = new();

    public string Add(
        string runId,
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int? maxLevel,
        bool restorePackages,
        IReadOnlySet<int> repositoryIds,
        IReadOnlySet<int> syncedRepoIds)
    {
        var approvalId = Guid.NewGuid().ToString("N");
        _pending[(workspaceId, contextId.Value)] = new PendingNormalPush(
            approvalId, runId, workspaceId, contextId, maxLevel, restorePackages,
            repositoryIds.ToHashSet(), syncedRepoIds.ToHashSet(), _time.GetUtcNow().UtcDateTime);
        return approvalId;
    }

    /// <summary>Returns the pending push and removes it, or null when the id is unknown, already used, expired or bound to another context.</summary>
    public PendingNormalPush? TryConsume(int workspaceId, WorkspaceFeatureContextId contextId, string approvalId)
    {
        var key = (workspaceId, contextId.Value);
        if (!_pending.TryGetValue(key, out var pending)
            || !string.Equals(pending.ApprovalId, approvalId, StringComparison.Ordinal)
            || !_pending.TryRemove(new KeyValuePair<(int, int), PendingNormalPush>(key, pending)))
            return null;

        return _time.GetUtcNow().UtcDateTime - pending.CreatedUtc > Lifetime ? null : pending;
    }
}
