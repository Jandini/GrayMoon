using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceActions
{
    private async Task LoadWorkspaceAsync()
    {
        isLoading = true;
        errorMessage = null;

        try
        {
            workspace = await WorkspaceRepository.GetByIdAsync(WorkspaceId);
            if (workspace == null)
            {
                errorMessage = "Workspace not found.";
                rows = [];
                return;
            }

            // No rows means no background refresh, auto-poll or hub-driven refresh can reach the CI provider either.
            _ciProvider = await CiProviderResolver.GetForWorkspaceAsync(WorkspaceId);
            if (!_ciProvider.IsEnabled)
            {
                errorMessage = "CI is not enabled for this workspace.";
                rows = [];
                return;
            }

            _groupByDependencyLevel = (await CapabilitiesResolver.GetAsync(WorkspaceId)).UsesDependencyGraph;

            var persistedActions = await _ciProvider.GetPersistedStatusesAsync(WorkspaceId, FeatureContextIdForCi);

            // For a Feature context, the checked-out branch and the dependency level live on
            // WorkspaceRepositoryContextState, not on the special Workspace's link - overlay them per repository so
            // Actions polls/displays the Feature's own branch and level instead of silently showing the Workspace's (§18/§2.2).
            // The snapshot of the viewed context also carries the upstream, outgoing commits and recorded parent branch
            // that pick the CI branch (UnpublishedBranchRules.CiBranch), for the special Workspace as well.
            var contextStateByRepositoryId = (await LinkListQueryService.GetAllSnapshotsAsync(WorkspaceId, _selectedContextId, isSpecialWorkspace: !_isFeatureContext))
                .ToDictionary(dto => dto.RepositoryId);

            rows = workspace.Repositories
                .Where(link => link.Repository != null && link.Repository.Connector != null)
                .OrderBy(link => link.Repository!.RepositoryName)
                .Select(link =>
                {
                    var contextState = contextStateByRepositoryId.GetValueOrDefault(link.RepositoryId);
                    var effectiveLink = _isFeatureContext
                        ? link.WithBranchOverride(contextState?.BranchName)
                        : link;

                    var ciBranch = ResolveCiBranch(effectiveLink, contextState);

                    var hasPersisted = persistedActions.TryGetValue(effectiveLink.RepositoryId, out var persisted);
                    var branchMatches = hasPersisted &&
                        string.Equals(persisted?.BranchName, ciBranch, StringComparison.OrdinalIgnoreCase);

                    List<WorkflowActionLine> workflowLines;
                    if (branchMatches && persisted != null && persisted.Workflows.Count > 0)
                    {
                        workflowLines = persisted.Workflows.Select(w => new WorkflowActionLine { Action = w }).ToList();
                    }
                    else
                    {
                        workflowLines = [new WorkflowActionLine()];
                    }

                    return new WorkspaceActionRow
                    {
                        Link = effectiveLink,
                        CiBranchName = ciBranch,
                        Repo = new GitHubRepositoryEntry
                        {
                            RepositoryId = link.Repository!.RepositoryId,
                            ConnectorName = link.Repository.Connector?.ConnectorName ?? string.Empty,
                            OrgName = link.Repository.OrgName,
                            RepositoryName = link.Repository.RepositoryName,
                            Visibility = link.Repository.Visibility,
                                Archived = link.Repository.Archived,
                            CloneUrl = link.Repository.CloneUrl
                        },
                        DependencyLevel = ResolveDependencyLevel(link, _isFeatureContext, contextState?.DependencyLevel),
                        IsWorkspaceRepository = link.Role == WorkspaceRepositoryRole.Workspace,
                        WorkflowLines = workflowLines,
                        IsVerified = branchMatches && hasPersisted
                    };
                })
                .ToList();

            // A new load (open, or another context) starts from the default chip, then hides success when this data already has a run.
            _showSuccess = true;
            _successFilterMode = SuccessFilterMode.Automatic;
            SyncSuccessFilterWithRunning();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading workspace {WorkspaceId}", WorkspaceId);
            errorMessage = "Failed to load workspace. Please try again later.";
            rows = [];
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task RefreshFromSyncAsync()
    {
        try
        {
            var rowsToRefresh = await ApplyFreshWorkspaceLinksAsync();
            await InvokeAsync(StateHasChanged);

            foreach (var row in rowsToRefresh.Where(r => !string.IsNullOrWhiteSpace(r.CiBranchName)))
                _ = RefreshRowAsync(row, _cts.Token);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error refreshing from sync for workspace {WorkspaceId}", WorkspaceId);
        }
    }

    /// <summary>
    /// After worker hook sync (e.g. push hook): refresh GitHub Actions for affected repos so running workflows and the live terminal appear.
    /// </summary>
    private async Task RefreshFromRepositorySyncAsync()
    {
        List<int> repositoryIds;
        lock (_pendingRepositorySyncIds)
        {
            repositoryIds = _pendingRepositorySyncIds.ToList();
            _pendingRepositorySyncIds.Clear();
        }

        if (repositoryIds.Count == 0)
            return;

        try
        {
            await ApplyFreshWorkspaceLinksAsync();
            await InvokeAsync(StateHasChanged);

            foreach (var repositoryId in repositoryIds)
            {
                var row = rows.FirstOrDefault(r => r.Repo.RepositoryId == repositoryId);
                if (row == null || string.IsNullOrWhiteSpace(row.CiBranchName))
                    continue;

                _ = RefreshRowAfterHookSyncAsync(row, _cts.Token);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error refreshing actions after repository sync for workspace {WorkspaceId}", WorkspaceId);
        }
    }

    /// <summary>Reloads workspace links from DB; returns rows whose branch changed (need full workflow reset).</summary>
    private async Task<List<WorkspaceActionRow>> ApplyFreshWorkspaceLinksAsync()
    {
        var rowsToRefresh = new List<WorkspaceActionRow>();

        await using var scope = ServiceScopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<WorkspaceRepository>();
        var freshWorkspace = await repo.GetByIdAsync(WorkspaceId);
        if (freshWorkspace == null)
            return rowsToRefresh;

        workspace = freshWorkspace;

        var linkListQuery = scope.ServiceProvider.GetRequiredService<GrayMoon.App.Services.Queries.IWorkspaceRepositoryLinkListQueryService>();
        var contextStateByRepositoryId = (await linkListQuery.GetAllSnapshotsAsync(WorkspaceId, _selectedContextId, isSpecialWorkspace: !_isFeatureContext))
            .ToDictionary(dto => dto.RepositoryId);

        var freshLinks = freshWorkspace.Repositories
            .Where(l => l.Repository != null && l.Repository.Connector != null)
            .ToDictionary(l => l.RepositoryId);

        foreach (var row in rows)
        {
            if (!freshLinks.TryGetValue(row.Repo.RepositoryId, out var sharedLink))
                continue;

            var contextState = contextStateByRepositoryId.GetValueOrDefault(row.Repo.RepositoryId);
            var freshLink = _isFeatureContext ? sharedLink.WithBranchOverride(contextState?.BranchName) : sharedLink;

            var freshCiBranch = ResolveCiBranch(freshLink, contextState);
            var branchChanged = !string.Equals(row.CiBranchName, freshCiBranch, StringComparison.OrdinalIgnoreCase);
            row.Link = freshLink;
            row.CiBranchName = freshCiBranch;
            row.DependencyLevel = ResolveDependencyLevel(sharedLink, _isFeatureContext, contextState?.DependencyLevel);
            row.IsWorkspaceRepository = sharedLink.Role == WorkspaceRepositoryRole.Workspace;

            if (branchChanged)
            {
                row.WorkflowLines = [new WorkflowActionLine()];
                row.ActionLatches.Clear();
                row.IsVerified = false;
                rowsToRefresh.Add(row);
            }
        }

        SyncSuccessFilterWithRunning();
        return rowsToRefresh;
    }

    /// <summary>The CI branch of one row from the snapshot of the viewed context; without a snapshot it is the checked-out branch.</summary>
    internal static string? ResolveCiBranch(WorkspaceRepositoryLink link, GrayMoon.App.Services.Queries.WorkspaceRepositoryLinkListItemDto? contextState) =>
        contextState == null
            ? link.BranchName
            : GrayMoon.App.Services.Policy.UnpublishedBranchRules.CiBranch(
                link.BranchName, contextState.BranchHasUpstream, contextState.OutgoingCommits, contextState.BaselineParentBranchName);

    /// <summary>One polling loop per repository: every hook sync used to start its own, side by side.</summary>
    private readonly GrayMoon.App.Components.Shared.KeyedRerunGuard<int> _hookRefreshGuard = new();

    private Task RefreshRowAfterHookSyncAsync(WorkspaceActionRow row, CancellationToken cancellationToken) =>
        _hookRefreshGuard.RunAsync(row.Repo.RepositoryId, () => TryRefreshUntilAnyWorkflowRunningAsync(row, cancellationToken));
}
