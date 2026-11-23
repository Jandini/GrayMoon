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

        /// <summary>
        /// Branch whose runs the row fetches, shows and starts: the parent branch while the checked-out branch has no
        /// upstream and no commits (see <see cref="GrayMoon.App.Services.Policy.UnpublishedBranchRules.CiBranch"/>), else the checked-out branch.
        /// </summary>
        public string? CiBranchName { get; set; }

        /// <summary>True when <see cref="CiBranchName"/> is the parent branch, not the checked-out branch.</summary>
        public bool ShowsParentBranch =>
            !string.IsNullOrWhiteSpace(CiBranchName) && !string.Equals(CiBranchName, Link.BranchName, StringComparison.Ordinal);

        /// <summary>Dependency level in the selected context (see <see cref="ResolveDependencyLevel"/>); null is "No dependencies".</summary>
        public int? DependencyLevel { get; set; }

        /// <summary>True for the Workspace repository, which has its own group above the levels.</summary>
        public bool IsWorkspaceRepository { get; set; }

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
    private int _refreshGeneration;
    private int _refreshTotal;
    private int _refreshCompleted;
    private bool isRerunningAll;
    private int _rerunTotal;
    private volatile int _rerunCompleted;

    /// <summary>Verb shown in <see cref="RerunAllOverlayMessage"/> for whichever bulk operation is in flight ("Re-running" vs "Starting").</summary>
    private string _bulkOperationVerb = "Re-running";

    /// <summary>True in the second phase of a bulk action: the workflows are started and the overlay waits for GitHub to list the runs.</summary>
    private bool _bulkWaiting;

    /// <summary>Cancelled by the overlay's Abort to end the wait for GitHub; null outside that wait.</summary>
    private CancellationTokenSource? _bulkWaitCts;
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

    /// <summary>
    /// Who last chose the success chip. <see cref="SuccessFilterMode.Automatic"/> hides success while a
    /// workflow is running and shows it again when none are. <see cref="SuccessFilterMode.ShownByUser"/> is a
    /// click that turns success on during a run, and lasts until those runs finish.
    /// <see cref="SuccessFilterMode.HiddenByUser"/> is a click that turns success off while nothing is running.
    /// </summary>
    internal enum SuccessFilterMode
    {
        Automatic,
        ShownByUser,
        HiddenByUser
    }

    private SuccessFilterMode _successFilterMode = SuccessFilterMode.Automatic;
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
        _bulkWaiting
            ? (_rerunCompleted == 0 ? (_bulkOperationVerb == "Aborting" ? "Waiting for actions to stop..." : "Waiting for actions to start...") : $"Waiting for {_rerunCompleted} of {_rerunTotal}")
            : _rerunCompleted == 0
            ? $"{_bulkOperationVerb} actions..."
            : $"{_bulkOperationVerb} {_rerunCompleted} of {_rerunTotal}";

    /// <summary>Same wording as Git Changes' header scan indicator.</summary>
    internal string RefreshStatusText =>
        _refreshTotal > 0 && _refreshCompleted > 0
            ? $"Refreshed {_refreshCompleted} of {_refreshTotal}"
            : "Refreshing...";

    /// <summary>Compact progress ("1 of 3") shown when the title row is too narrow for <see cref="RefreshStatusText"/>.</summary>
    internal string RefreshStatusShortText =>
        _refreshTotal > 0 && _refreshCompleted > 0
            ? $"{_refreshCompleted} of {_refreshTotal}"
            : "";

    internal bool HasSearchFilter => !string.IsNullOrWhiteSpace(searchTerm);

    /// <summary>Workflow table rows visible with current status filters (before text search).</summary>
    internal int VisibleWorkflowLineCount => rows.Sum(r => LinesForGrid(r).Count());

    /// <summary>Workflow table rows visible after status filters and text search.</summary>
    internal int FilteredWorkflowLineCount => rows.Sum(r => LinesForTable(r).Count());

    /// <summary>True when the workspace has repos but no row is visible (status filters and/or search).</summary>
    internal bool NoVisibleWorkflowRows =>
        !isLoading && rows.Count > 0 && !rows.Any(row => LinesForTable(row).Any());
}
