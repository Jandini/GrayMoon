using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Queries;
using GrayMoon.Application.Features;
namespace GrayMoon.App.Services.Ui;
public sealed record NotificationRepo(
    int RepositoryId,
    string RepositoryName,
    int UnmatchedDeps,
    int TotalDeps,
    int OutgoingCommits,
    int IncomingCommits,
    bool NewBranchNoPush,
    IReadOnlyList<(string PackageId, string CurrentVersion, string NewVersion)> MismatchedDeps,
    string? BranchName,
    string? DefaultBranchName);
public sealed record WorkspaceNotification(
    int WorkspaceId,
    string WorkspaceName,
    bool HasUnmatchedDependencies,
    bool IsPushRecommended,
    bool HasIncomingCommits,
    IReadOnlyList<NotificationRepo> Repos,
    int? LowestLevelNeedingWork,
    int TotalActionRepoCount);
public sealed class WorkspacePendingActionsService(
    IWorkspaceSelectedFeatureContextService selectedContextService,
    IWorkspaceFeatureContextResolver contextResolver,
    IWorkspaceRepositoryLinkListQueryService linkListQuery)
{
    public const int MaxListedActionRepos = 100;
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
    /// <summary>Reloads the workspace's repository state for the selected context (the special Workspace or a Feature) and recomputes the notification for it immediately, so callers that just changed repository membership (e.g. removing a repository from a workspace) don't have to wait for the next Worker-driven WorkspaceSynced hub event.</summary>
    public async Task RefreshAsync(int workspaceId, WorkspaceRepository workspaceRepository, WorkspaceProjectRepository workspaceProjectRepository)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
        {
            OnWorkspaceSynced(null, workspaceId);
            return;
        }
        var contextId = await selectedContextService.GetSelectedAsync(workspaceId)
            ?? await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId);
        WorkspaceFeatureContextInfo contextInfo;
        try
        {
            contextInfo = await contextResolver.GetRequiredAsync(contextId, workspaceId);
        }
        catch (InvalidOperationException)
        {
            // The persisted selection points at a context that no longer exists (e.g. a removed Feature).
            contextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId);
            contextInfo = await contextResolver.GetRequiredAsync(contextId, workspaceId);
        }
        var links = await linkListQuery.GetAllSnapshotsAsync(workspaceId, contextId, contextInfo.IsSpecialWorkspace);
        var mismatchPayloads = await workspaceProjectRepository.GetSyncDependenciesPayloadAsync(workspaceId, contextId.Value);
        var mismatchedDeps = mismatchPayloads.ToDictionary(
            p => p.RepoId,
            p => (IReadOnlyList<(string PackageId, string CurrentVersion, string NewVersion)>)p.ProjectUpdates
                .SelectMany(u => u.PackageUpdates)
                .GroupBy(x => x.PackageId)
                .Select(g => g.First())
                .ToList());
        var notification = ComputeNotification(workspaceId, workspace.Name, links, mismatchedDeps);
        OnWorkspaceSynced(notification, workspaceId);
    }
    public static WorkspaceNotification? ComputeNotification(
        int workspaceId,
        string workspaceName,
        IReadOnlyList<WorkspaceRepositoryLinkListItemDto> links,
        IReadOnlyDictionary<int, IReadOnlyList<(string PackageId, string CurrentVersion, string NewVersion)>>? mismatchedDeps = null)
    {
        bool hasUnmatched = links.Any(wr => !IsOnTag(wr) && ((wr.UnmatchedDeps ?? 0) > 0 || (wr.OutOfDateFileRepos ?? 0) > 0));
        bool isPushRecommended = links.Any(wr => !IsOnTag(wr) && ((wr.OutgoingCommits ?? 0) > 0 || wr.BranchHasUpstream == false));
        bool hasIncoming = links.Any(wr =>
            !IsOnTag(wr)
            && (wr.IncomingCommits ?? 0) > 0);
        if (!hasUnmatched && !isPushRecommended && !hasIncoming)
            return null;
        var actionLinks = links
            .Where(wr => !IsOnTag(wr) && (
                (wr.UnmatchedDeps ?? 0) > 0 ||
                (wr.OutOfDateFileRepos ?? 0) > 0 ||
                (wr.OutgoingCommits ?? 0) > 0 ||
                wr.BranchHasUpstream == false ||
                (wr.IncomingCommits ?? 0) > 0))
            .OrderBy(wr => wr.DependencyLevel ?? 0)
            .ThenBy(wr => wr.RepositoryName)
            .ToList();
        var totalActionRepoCount = actionLinks.Count;
        var repos = actionLinks
            .Take(MaxListedActionRepos)
            .Select(wr =>
            {
                var depLines = mismatchedDeps != null && mismatchedDeps.TryGetValue(wr.RepositoryId, out var lines)
                    ? lines
                    : (IReadOnlyList<(string, string, string)>)Array.Empty<(string, string, string)>();
                return new NotificationRepo(
                    wr.RepositoryId,
                    wr.RepositoryName,
                    wr.UnmatchedDeps ?? 0,
                    wr.Dependencies ?? 0,
                    wr.OutgoingCommits ?? 0,
                    wr.IncomingCommits ?? 0,
                    wr.BranchHasUpstream == false,
                    depLines,
                    wr.BranchName,
                    wr.DefaultBranchName);
            })
            .ToList();
        var lowestLevel = links
            .Where(wr => !IsOnTag(wr) && ((wr.UnmatchedDeps ?? 0) > 0 || (wr.OutOfDateFileRepos ?? 0) > 0))
            .Min(wr => wr.DependencyLevel);
        return new WorkspaceNotification(
            workspaceId,
            workspaceName,
            hasUnmatched,
            isPushRecommended,
            hasIncoming,
            repos,
            lowestLevel,
            totalActionRepoCount);
    }
    private static bool IsOnTag(WorkspaceRepositoryLinkListItemDto link) => !string.IsNullOrWhiteSpace(link.CheckedOutTag);
}
