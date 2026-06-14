using GrayMoon.App.Models;

namespace GrayMoon.App.Services;

public sealed record WorkspaceNotification(
    int WorkspaceId,
    string WorkspaceName,
    bool HasUnmatchedDependencies,
    bool IsPushRecommended,
    bool HasIncomingCommits);

public sealed class WorkspacePendingActionsService
{
    private readonly List<WorkspaceNotification> _notifications = new();

    public IReadOnlyList<WorkspaceNotification> Notifications => _notifications;

    public event Action? Changed;

    public void OnWorkspaceSynced(WorkspaceNotification? notification, int workspaceId)
    {
        _notifications.RemoveAll(n => n.WorkspaceId == workspaceId);
        if (notification != null)
        {
            if (_notifications.Count >= 2)
                _notifications.RemoveAt(0);
            _notifications.Add(notification);
        }
        Changed?.Invoke();
    }

    public void Dismiss(int workspaceId)
    {
        _notifications.RemoveAll(n => n.WorkspaceId == workspaceId);
        Changed?.Invoke();
    }

    public static WorkspaceNotification? ComputeNotification(
        int workspaceId,
        string workspaceName,
        IReadOnlyList<WorkspaceRepositoryLink> links)
    {
        bool hasUnmatched = links.Any(wr => !wr.IsOnTag && (wr.UnmatchedDeps ?? 0) > 0);
        bool isPushRecommended = links.Any(wr => !wr.IsOnTag && ((wr.OutgoingCommits ?? 0) > 0 || wr.BranchHasUpstream == false));
        bool hasIncoming = links.Any(wr =>
            !wr.IsOnTag
            && (wr.IncomingCommits ?? 0) > 0
            && !string.IsNullOrWhiteSpace(wr.BranchName)
            && !string.IsNullOrWhiteSpace(wr.DefaultBranchName)
            && string.Equals(wr.BranchName, wr.DefaultBranchName, StringComparison.Ordinal));
        if (!hasUnmatched && !isPushRecommended && !hasIncoming)
            return null;
        return new WorkspaceNotification(workspaceId, workspaceName, hasUnmatched, isPushRecommended, hasIncoming);
    }
}
