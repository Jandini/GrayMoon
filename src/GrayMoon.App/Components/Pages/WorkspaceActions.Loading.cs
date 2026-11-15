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
            var contextStateByRepositoryId = _isFeatureContext
                ? (await LinkListQueryService.GetAllSnapshotsAsync(WorkspaceId, _selectedContextId, isSpecialWorkspace: false))
                    .ToDictionary(dto => dto.RepositoryId)
                : [];

            rows = workspace.Repositories
                .Where(link => link.Repository != null && link.Repository.Connector != null)
                .OrderBy(link => link.Repository!.RepositoryName)
                .Select(link =>
                {
                    var contextState = contextStateByRepositoryId.GetValueOrDefault(link.RepositoryId);
                    var effectiveLink = _isFeatureContext
                        ? link.WithBranchOverride(contextState?.BranchName)
                        : link;

                    var hasPersisted = persistedActions.TryGetValue(effectiveLink.RepositoryId, out var persisted);
                    var branchMatches = hasPersisted &&
                        string.Equals(persisted?.BranchName, effectiveLink.BranchName, StringComparison.OrdinalIgnoreCase);

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

            foreach (var row in rowsToRefresh.Where(r => !string.IsNullOrWhiteSpace(r.Link.BranchName)))
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
                if (row == null || string.IsNullOrWhiteSpace(row.Link.BranchName))
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

        Dictionary<int, GrayMoon.App.Services.Queries.WorkspaceRepositoryLinkListItemDto> contextStateByRepositoryId = [];
        if (_isFeatureContext)
        {
            var linkListQuery = scope.ServiceProvider.GetRequiredService<GrayMoon.App.Services.Queries.IWorkspaceRepositoryLinkListQueryService>();
            contextStateByRepositoryId = (await linkListQuery.GetAllSnapshotsAsync(WorkspaceId, _selectedContextId, isSpecialWorkspace: false))
                .ToDictionary(dto => dto.RepositoryId);
        }

        var freshLinks = freshWorkspace.Repositories
            .Where(l => l.Repository != null && l.Repository.Connector != null)
            .ToDictionary(l => l.RepositoryId);

        foreach (var row in rows)
        {
            if (!freshLinks.TryGetValue(row.Repo.RepositoryId, out var sharedLink))
                continue;

            var contextState = contextStateByRepositoryId.GetValueOrDefault(row.Repo.RepositoryId);
            var freshLink = _isFeatureContext ? sharedLink.WithBranchOverride(contextState?.BranchName) : sharedLink;

            var branchChanged = !string.Equals(row.Link.BranchName, freshLink.BranchName, StringComparison.OrdinalIgnoreCase);
            row.Link = freshLink;
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

        return rowsToRefresh;
    }

    private async Task RefreshRowAfterHookSyncAsync(WorkspaceActionRow row, CancellationToken cancellationToken)
    {
        await TryRefreshUntilAnyWorkflowRunningAsync(row, cancellationToken);
    }
}
