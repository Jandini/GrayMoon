using System.Net;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceActions : IDisposable
{
    [Parameter] public int WorkspaceId { get; set; }

    [Inject] private WorkspaceActionService ActionService { get; set; } = null!;
    [Inject] private GitHubActionsService GitHubActionsService { get; set; } = null!;
    [Inject] private WorkspaceRepository WorkspaceRepository { get; set; } = null!;
    [Inject] private IOptions<WorkspaceOptions> WorkspaceOptions { get; set; } = null!;
    [Inject] private IServiceScopeFactory ServiceScopeFactory { get; set; } = null!;
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    [Inject] private ILogger<WorkspaceActions> Logger { get; set; } = null!;

    private int MaxConcurrency => Math.Max(1, WorkspaceOptions.Value.MaxParallelOperations);

    internal sealed class WorkflowActionLine
    {
        public ActionStatusInfo? Action { get; set; }
        public bool RunInProgress { get; set; }
    }

    internal sealed class WorkspaceActionRow
    {
        public required WorkspaceRepositoryLink Link { get; set; }
        public required GitHubRepositoryEntry Repo { get; init; }

        public List<WorkflowActionLine> WorkflowLines { get; set; } = [];

        public bool IsVerified { get; set; }
        public bool IsRefreshing { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private Workspace? workspace;
    private List<WorkspaceActionRow> rows = [];
    private string? errorMessage;
    private bool isLoading = true;
    private bool isRefreshing;
    private bool isRerunningAll;
    private int _rerunTotal;
    private volatile int _rerunCompleted;
    private CancellationTokenSource _cts = new();
    private HubConnection? _hubConnection;
    private CancellationTokenSource? _syncDebounceCts;
    private bool _autoPollRunning;
    private const int SyncDebounceMs = 500;
    private const int AutoPollIntervalMs = 5000;

    internal bool HasFailedRows => rows.Any(row =>
        row.WorkflowLines.Any(line =>
            IsLineFailedForBranch(row, line)));

    internal int ErrorCount => rows.Count(r => !string.IsNullOrWhiteSpace(r.ErrorMessage));

    internal int FailedCount => rows.Sum(row => row.WorkflowLines.Count(line =>
        string.IsNullOrWhiteSpace(row.ErrorMessage) && IsLineFailedForBranch(row, line)));

    internal int RunningCount => rows.Sum(row => row.WorkflowLines.Count(line =>
        string.IsNullOrWhiteSpace(row.ErrorMessage) && IsLineRunningForBranch(row, line)));

    internal int SuccessCount => rows.Sum(row => row.WorkflowLines.Count(line =>
        string.IsNullOrWhiteSpace(row.ErrorMessage) && IsLineSuccessForBranch(row, line)));

    internal int NoneCount => rows.Sum(row => row.WorkflowLines.Count(line =>
        string.IsNullOrWhiteSpace(row.ErrorMessage) && IsLineNoneForBranch(row, line)));

    internal string RerunAllOverlayMessage =>
        _rerunCompleted == 0
            ? "Re-running actions..."
            : $"Re-running {_rerunCompleted} of {_rerunTotal}";

    protected override async Task OnInitializedAsync()
    {
        await LoadWorkspaceAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        if (!isLoading && rows.Count > 0)
            StartBackgroundRefresh();

        if (workspace != null && errorMessage == null)
        {
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(NavigationManager.ToAbsoluteUri("/hubs/workspace-sync"))
                .WithAutomaticReconnect()
                .Build();

            _hubConnection.On<int>("WorkspaceSynced", async (workspaceId) =>
            {
                if (workspaceId != WorkspaceId) return;

                _syncDebounceCts?.Cancel();
                _syncDebounceCts?.Dispose();
                _syncDebounceCts = new CancellationTokenSource();
                var cts = _syncDebounceCts;
                try
                {
                    await Task.Delay(SyncDebounceMs, cts.Token);
                    await InvokeAsync(RefreshFromSyncAsync);
                }
                catch (OperationCanceledException) { /* debounced */ }
                finally
                {
                    if (cts == _syncDebounceCts)
                    {
                        _syncDebounceCts?.Dispose();
                        _syncDebounceCts = null;
                    }
                }
            });

            await _hubConnection.StartAsync();
        }
    }

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

            var persistedActions = await ActionService.GetPersistedActionsForWorkspaceAsync(WorkspaceId);

            rows = workspace.Repositories
                .Where(link => link.Repository != null && link.Repository.Connector != null)
                .OrderBy(link => link.Repository!.RepositoryName)
                .Select(link =>
                {
                    var hasPersisted = persistedActions.TryGetValue(link.RepositoryId, out var persisted);
                    var branchMatches = hasPersisted &&
                        string.Equals(persisted?.BranchName, link.BranchName, StringComparison.OrdinalIgnoreCase);

                    List<WorkflowActionLine> workflowLines;
                    if (branchMatches && persisted != null && persisted.Workflows.Count > 0)
                    {
                        workflowLines = persisted.Workflows
                            .Select(w => new WorkflowActionLine { Action = w })
                            .ToList();
                    }
                    else
                    {
                        workflowLines = [new WorkflowActionLine()];
                    }

                    return new WorkspaceActionRow
                    {
                        Link = link,
                        Repo = new GitHubRepositoryEntry
                        {
                            RepositoryId = link.Repository!.RepositoryId,
                            ConnectorName = link.Repository.Connector?.ConnectorName ?? string.Empty,
                            OrgName = link.Repository.OrgName,
                            RepositoryName = link.Repository.RepositoryName,
                            Visibility = link.Repository.Visibility,
                            CloneUrl = link.Repository.CloneUrl
                        },
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
            await using var scope = ServiceScopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<WorkspaceRepository>();
            var freshWorkspace = await repo.GetByIdAsync(WorkspaceId);
            if (freshWorkspace == null) return;

            workspace = freshWorkspace;

            var freshLinks = freshWorkspace.Repositories
                .Where(l => l.Repository != null && l.Repository.Connector != null)
                .ToDictionary(l => l.RepositoryId);

            var rowsToRefresh = new List<WorkspaceActionRow>();

            foreach (var row in rows)
            {
                if (!freshLinks.TryGetValue(row.Repo.RepositoryId, out var freshLink)) continue;

                var branchChanged = !string.Equals(row.Link.BranchName, freshLink.BranchName, StringComparison.OrdinalIgnoreCase);
                row.Link = freshLink;

                if (branchChanged)
                {
                    row.WorkflowLines = [new WorkflowActionLine()];
                    row.IsVerified = false;
                    rowsToRefresh.Add(row);
                }
            }

            await InvokeAsync(StateHasChanged);

            foreach (var row in rowsToRefresh.Where(r => !string.IsNullOrWhiteSpace(r.Link.BranchName)))
            {
                _ = RefreshRowAsync(row, _cts.Token);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error refreshing from sync for workspace {WorkspaceId}", WorkspaceId);
        }
    }

    private void StartBackgroundRefresh()
    {
        foreach (var row in rows.Where(r => !string.IsNullOrWhiteSpace(r.Link.BranchName)))
        {
            _ = RefreshRowAsync(row, _cts.Token);
        }
    }

    private void EnsureAutoPollRunning()
    {
        if (_autoPollRunning) return;
        _autoPollRunning = true;
        _ = AutoPollLoopAsync(_cts.Token);
    }

    private async Task AutoPollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(AutoPollIntervalMs, cancellationToken);

                if (cancellationToken.IsCancellationRequested) break;

                var runningRows = rows
                    .Where(r =>
                        !string.IsNullOrWhiteSpace(r.Link.BranchName) &&
                        r.WorkflowLines.Any(line => IsLineRunningForBranch(r, line)))
                    .ToList();

                if (runningRows.Count == 0) break;

                var tasks = runningRows.Select(row => RefreshRowAsync(row, cancellationToken)).ToList();
                await Task.WhenAll(tasks);
            }
        }
        catch (OperationCanceledException) { /* page disposed */ }
        finally
        {
            _autoPollRunning = false;
        }
    }

    private async Task RefreshRowAsync(WorkspaceActionRow row, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.Link.BranchName)) return;

        try
        {
            row.IsRefreshing = true;

            var list = await ActionService.FetchAndPersistAsync(
                row.Link.WorkspaceRepositoryId,
                row.Repo,
                row.Link.BranchName!,
                cancellationToken);

            if (!cancellationToken.IsCancellationRequested && list != null)
            {
                row.WorkflowLines = list
                    .Select(w => new WorkflowActionLine { Action = w })
                    .ToList();
                row.IsVerified = true;
                row.ErrorMessage = null;

                if (row.WorkflowLines.Any(line => IsLineRunningForBranch(row, line)))
                    EnsureAutoPollRunning();
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.LogError(ex, "Error refreshing action for {Repo}/{Branch}", row.Repo.RepositoryName, row.Link.BranchName);
            row.ErrorMessage = GetFriendlyErrorMessage(ex);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                row.IsRefreshing = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    internal async Task RefreshAllAsync()
    {
        if (workspace == null || rows.Count == 0) return;

        isRefreshing = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();

            var token = _cts.Token;
            var tasks = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Link.BranchName))
                .Select(row => RefreshRowAsync(row, token))
                .ToList();

            await Task.WhenAll(tasks);
        }
        finally
        {
            isRefreshing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    internal async Task RerunWorkflowAsync(WorkspaceActionRow row, WorkflowActionLine line)
    {
        if (line.Action?.RunId == null) return;

        row.ErrorMessage = null;
        line.RunInProgress = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            var actionEntry = BuildActionEntry(row, line);
            await GitHubActionsService.RerunWorkflowAsync(actionEntry);
            await RefreshRowAsync(row, _cts.Token);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error re-running workflow for {Repo}", row.Repo.RepositoryName);
            row.ErrorMessage = GetFriendlyErrorMessage(ex);
            await InvokeAsync(StateHasChanged);
        }
        finally
        {
            line.RunInProgress = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    internal async Task RunWorkflowAsync(WorkspaceActionRow row, WorkflowActionLine line)
    {
        if (line.Action?.WorkflowId == null) return;

        row.ErrorMessage = null;
        line.RunInProgress = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            var actionEntry = BuildActionEntry(row, line);
            await GitHubActionsService.RunWorkflowAsync(actionEntry);
            await Task.Delay(2000);
            await RefreshRowAsync(row, _cts.Token);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error running workflow for {Repo}", row.Repo.RepositoryName);
            row.ErrorMessage = GetFriendlyErrorMessage(ex);
            await InvokeAsync(StateHasChanged);
        }
        finally
        {
            line.RunInProgress = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private static GitHubActionEntry BuildActionEntry(WorkspaceActionRow row, WorkflowActionLine line) => new()
    {
        RunId = line.Action?.RunId ?? 0,
        WorkflowId = line.Action?.WorkflowId ?? 0,
        ConnectorName = row.Repo.ConnectorName,
        Owner = row.Repo.OrgName ?? string.Empty,
        RepositoryName = row.Repo.RepositoryName,
        WorkflowName = line.Action?.WorkflowName ?? string.Empty,
        Status = string.Empty,
        HeadBranch = row.Link.BranchName
    };

    internal async Task RerunAllFailedAsync()
    {
        var failedPairs = new List<(WorkspaceActionRow Row, WorkflowActionLine Line)>();
        foreach (var row in rows)
        {
            foreach (var line in row.WorkflowLines)
            {
                if (IsLineFailedForBranch(row, line) && (line.Action?.RunId ?? 0) > 0)
                    failedPairs.Add((row, line));
            }
        }

        if (failedPairs.Count == 0) return;

        _rerunTotal = failedPairs.Count;
        _rerunCompleted = 0;
        isRerunningAll = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
            var tasks = failedPairs.Select(async pair =>
            {
                await semaphore.WaitAsync(_cts.Token);
                try
                {
                    var actionEntry = BuildActionEntry(pair.Row, pair.Line);
                    await GitHubActionsService.RerunWorkflowAsync(actionEntry);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error re-running workflow for {Repo}", pair.Row.Repo.RepositoryName);
                }
                finally
                {
                    semaphore.Release();
                    Interlocked.Increment(ref _rerunCompleted);
                    await InvokeAsync(StateHasChanged);
                }
            });
            await Task.WhenAll(tasks);
        }
        finally
        {
            isRerunningAll = false;
            await InvokeAsync(StateHasChanged);
            _ = RefreshAllAsync();
        }
    }

    private static string GetFriendlyErrorMessage(Exception ex) =>
        ex switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
                "Unauthorized (401). Check the connector token on the Connectors page.",
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
                "Forbidden (403). The token does not have permission to access GitHub Actions.",
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
                "Not found (404). The repository or workflow was not found.",
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
                "Rate limited (429). Too many requests to GitHub. Try again later.",
            HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable } =>
                "GitHub service unavailable (503). Try again later.",
            HttpRequestException httpEx =>
                $"GitHub API error: {httpEx.Message}",
            _ => ex.Message
        };

    internal static int GetStatusSortOrder(WorkspaceActionRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.ErrorMessage)) return 0;
        var status = GetEffectiveStatusForSort(row);
        return status switch
        {
            "failed" => 1,
            "running" => 2,
            "success" => 3,
            _ => 4
        };
    }

    private static string? GetEffectiveStatusForSort(WorkspaceActionRow row)
    {
        var order = 4;
        string? worst = null;
        foreach (var line in row.WorkflowLines)
        {
            var a = line.Action;
            if (a == null || !string.Equals(a.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase))
                continue;
            var o = a.Status switch
            {
                "failed" => 1,
                "running" => 2,
                "success" => 3,
                _ => 4
            };
            if (o < order)
            {
                order = o;
                worst = a.Status;
            }
        }

        return worst;
    }

    private static bool IsLineFailedForBranch(WorkspaceActionRow row, WorkflowActionLine line) =>
        line.Action != null &&
        string.Equals(line.Action.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(line.Action.Status, "failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsLineRunningForBranch(WorkspaceActionRow row, WorkflowActionLine line) =>
        line.Action != null &&
        string.Equals(line.Action.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(line.Action.Status, "running", StringComparison.OrdinalIgnoreCase);

    private static bool IsLineSuccessForBranch(WorkspaceActionRow row, WorkflowActionLine line) =>
        line.Action != null &&
        string.Equals(line.Action.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(line.Action.Status, "success", StringComparison.OrdinalIgnoreCase);

    private static bool IsLineNoneForBranch(WorkspaceActionRow row, WorkflowActionLine line) =>
        line.Action == null ||
        !string.Equals(line.Action.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(line.Action.Status, "none", StringComparison.OrdinalIgnoreCase);

    internal static bool CanRerun(WorkspaceActionRow row, WorkflowActionLine line) =>
        IsLineFailedForBranch(row, line) && (line.Action?.RunId ?? 0) > 0;

    internal static bool CanRun(WorkspaceActionRow row, WorkflowActionLine line) =>
        (line.Action?.SupportsWorkflowDispatch ?? false) &&
        (line.Action?.WorkflowId ?? 0) > 0 &&
        !string.IsNullOrWhiteSpace(row.Link.BranchName);

    internal static IEnumerable<WorkflowActionLine> LinesForDisplay(WorkspaceActionRow row)
    {
        if (row.WorkflowLines.Count > 0)
            return row.WorkflowLines;
        return [new WorkflowActionLine()];
    }

    internal static string GroupStripeClass(int groupIndex) =>
        (groupIndex % 2 == 0) ? "actions-group-even" : "actions-group-odd";

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _syncDebounceCts?.Cancel();
        _syncDebounceCts?.Dispose();
        _ = _hubConnection?.StopAsync();
        _ = _hubConnection?.DisposeAsync().AsTask();
    }
}
