using GrayMoon.App.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceActions
{
    internal sealed class WorkflowActionLine
    {
        public ActionStatusInfo? Action { get; set; }
        public bool RunInProgress { get; set; }
        public bool AbortInProgress { get; set; }
    }

    /// <summary>Survives <see cref="RefreshRowAsync"/> replacing <see cref="WorkflowActionLine"/> instances so Run/Abort cannot double-post or flicker enabled mid-refresh.</summary>
    internal sealed class WorkflowActionButtonLatch
    {
        public bool RunPending { get; set; }
        public bool AbortPending { get; set; }
        public bool RerunPending { get; set; }
    }

    internal sealed class WorkspaceActionRow
    {
        public required WorkspaceRepositoryLink Link { get; set; }
        public required GitHubRepositoryEntry Repo { get; init; }

        public List<WorkflowActionLine> WorkflowLines { get; set; } = [];

        public bool IsVerified { get; set; }
        public bool IsRefreshing { get; set; }
        public string? ErrorMessage { get; set; }

        public Dictionary<long, WorkflowActionButtonLatch> ActionLatches { get; } = new();
    }

    private Workspace? workspace;
    private List<WorkspaceActionRow> rows = [];
    private string? errorMessage;
    private bool isLoading = true;
    private bool isRefreshing;
    private bool isRerunningAll;
    private int _rerunTotal;
    private volatile int _rerunCompleted;

    /// <summary>Verb shown in <see cref="RerunAllOverlayMessage"/> for whichever bulk operation is in flight ("Re-running" vs "Starting").</summary>
    private string _bulkOperationVerb = "Re-running";
    private CancellationTokenSource _cts = new();
    private HubConnection? _hubConnection;
    private CancellationTokenSource? _syncDebounceCts;
    private CancellationTokenSource? _repositorySyncDebounceCts;
    private readonly HashSet<int> _pendingRepositorySyncIds = [];
    private bool _autoPollRunning;
    private CancellationTokenSource? _autoPollWakeCts;
    private volatile bool _disposed;
    private const int SyncDebounceMs = 500;

    /// <summary>Poll cadence scales with AppActivityStateService: fast while the user is actively looking at
    /// the page, back to today's rate while idle, way down while the tab is backgrounded - and resuming
    /// activity always wakes the loop immediately (see OnActivityBecameActive) rather than waiting out
    /// whatever slower delay was in flight.</summary>
    private const int AutoPollIntervalActiveMs = 2000;
    private const int AutoPollIntervalIdleMs = 5000;
    private const int AutoPollIntervalHiddenMs = 30000;

    /// <summary>After dispatch/rerun, GitHub may not list the new run immediately; refresh until we see running or exhaust attempts.</summary>
    private const int RunWorkflowVisibilityMaxAttempts = 10;

    private const int RunWorkflowVisibilityFirstDelayMs = 2000;
    private const int RunWorkflowVisibilityRetryDelayMs = 3000;

    private bool _showErrors = true;
    private bool _showFailed = true;
    private bool _showAborted = true;
    private bool _showRunning = true;
    private bool _showSuccess = true;
    private bool _showNone;
    private bool _isTogglingAiFilter;
    private string searchTerm = string.Empty;
    private string? _appliedSearchQuery;

    private bool _logsModalVisible;
    private string? _logsConnectorName;
    private string? _logsOwner;
    private string? _logsRepo;
    private long _logsRunId;
    private string? _logsWorkflowName;

    private bool _rerunMenuOpen;

    /// <summary>
    /// Latch key (see <see cref="GetLineLatchKey"/>) of the per-row Run-again menu currently open, or null when none is.
    /// The menu itself is rendered with <c>position: fixed</c> at <see cref="_rerunLineMenuTop"/>/<see cref="_rerunLineMenuLeft"/>
    /// (captured from the click event) rather than anchored via CSS to its trigger button, because the actions grid's
    /// scrollable <c>tbody</c> (and its ancestors) clip absolutely-positioned overflow - see WorkspaceActions.razor.css.
    /// </summary>
    private long? _openRerunLineMenuKey;

    private double _rerunLineMenuTop;
    private double _rerunLineMenuLeft;

    private enum ActionLineFilterBucket
    {
        Failed,
        Running,
        Aborted,
        Success,
        None
    }

    /// <summary>False (default) keeps AI workflows in memory for <see cref="AiWorkflowCount"/> but out of the grid and status chips.</summary>
    internal bool IncludeAiWorkflows => workspace?.ExcludeAiWorkflows != true;

    internal bool HasFailedRows => rows.Any(row =>
        row.WorkflowLines.Any(line =>
            IsLineIncludedWhenAiFiltered(line) && IsLineFailedForBranch(row, line)));

    /// <summary>True when at least one failed workflow (anywhere in the grid) also supports workflow_dispatch, so the bulk "Run again" option has something to act on.</summary>
    internal bool HasFailedRowsSupportingRunAgain => rows.Any(row =>
        row.WorkflowLines.Any(line => IsLineIncludedWhenAiFiltered(line) && CanRunAgain(row, line)));

    internal int ErrorCount => rows.Count(r => !string.IsNullOrWhiteSpace(r.ErrorMessage));

    internal int FailedCount => rows.Sum(row => row.WorkflowLines.Count(line =>
        CountsTowardStatus(row, line) && IsLineFailedForBranch(row, line)));

    internal int RunningCount => rows.Sum(row => row.WorkflowLines.Count(line =>
        CountsTowardStatus(row, line) && IsLineRunningForBranch(row, line)));

    internal int AbortedCount => rows.Sum(row => row.WorkflowLines.Count(line =>
        CountsTowardStatus(row, line) && IsLineAbortedForBranch(row, line)));

    internal int SuccessCount => rows.Sum(row => row.WorkflowLines.Count(line =>
        CountsTowardStatus(row, line) && IsLineSuccessForBranch(row, line)));

    internal int NoneCount => rows.Sum(row => row.WorkflowLines.Count(line =>
        CountsTowardStatus(row, line) && IsLineNoneForBranch(row, line)));

    /// <summary>Count of AI-driven workflow lines pulled for the workspace. Shown on the AI badge even when that badge is off.</summary>
    internal int AiWorkflowCount => rows.Sum(row => row.WorkflowLines.Count(line =>
        string.IsNullOrWhiteSpace(row.ErrorMessage) && IsAiWorkflow(line.Action)));

    internal string RerunAllOverlayMessage =>
        _rerunCompleted == 0
            ? $"{_bulkOperationVerb} actions..."
            : $"{_bulkOperationVerb} {_rerunCompleted} of {_rerunTotal}";

    internal bool HasSearchFilter => !string.IsNullOrWhiteSpace(searchTerm);

    /// <summary>Workflow table rows visible with current status filters (before text search).</summary>
    internal int VisibleWorkflowLineCount => rows.Sum(r => LinesForGrid(r).Count());

    /// <summary>Workflow table rows visible after status filters and text search.</summary>
    internal int FilteredWorkflowLineCount => rows.Sum(r => LinesForTable(r).Count());

    /// <summary>True when the workspace has repos but no row is visible (status filters and/or search).</summary>
    internal bool NoVisibleWorkflowRows =>
        !isLoading && rows.Count > 0 && !rows.Any(row => LinesForTable(row).Any());
}
