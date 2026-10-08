using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Components.Shared;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Ui;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Components.Modals;

/// <summary>
/// "Restore Workspace" dialog (D14): pick an imported GitHub repository, see what its <c>.graymoon.json</c> will
/// restore (checked read-only through the connector before anything is created), choose a Workspace name, then
/// restore as a background job under the page overlay, run the normal full Sync and open the new Workspace.
/// </summary>
public sealed partial class RestoreWorkspaceModal : ComponentBase, IDisposable
{
    [Parameter] public bool IsVisible { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }

    /// <summary>Raised after a restore finished (success or not) so the host can refresh its list.</summary>
    [Parameter] public EventCallback OnRestored { get; set; }

    [Inject] private IDbContextFactory<AppDbContext> DbContextFactory { get; set; } = default!;
    [Inject] private WorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private IWorkerBridge WorkerBridge { get; set; } = default!;
    [Inject] private WorkerConnectionTracker WorkerConnectionTracker { get; set; } = default!;
    [Inject] private IBackgroundJobService JobService { get; set; } = default!;
    [Inject] private IScopedServiceExecutor ScopedExecutor { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<RestoreWorkspaceModal> Logger { get; set; } = default!;

    private ElementReference _modalElement;
    private IReadOnlyList<RestoreRepositoryChoice> _choices = Array.Empty<RestoreRepositoryChoice>();
    private IReadOnlyList<RepositoryPickerChoice> _pickerChoices = Array.Empty<RepositoryPickerChoice>();
    private HashSet<string> _existingNames = new(StringComparer.OrdinalIgnoreCase);
    private int? _repositoryId;
    private string _name = string.Empty;
    private string _autoName = string.Empty;
    private WorkspaceDirectoryState? _folder;
    private bool _checking;
    private int _checkRequestId;
    private CancellationTokenSource? _checkCts;
    private readonly RestorePreflightGate _preflightGate = new();
    private CancellationTokenSource? _preflightCts;
    private RestoreWorkspacePreflight? _preflight;
    private string? _error;
    private string? _errorResidue;
    private bool _busy;
    private bool _wasVisible;
    private bool _focusPending;
    private bool _disposed;
    private RestoreResultPanel? _panel;
    private int? _restoredWorkspaceId;

    private string? NameError => string.IsNullOrWhiteSpace(_name) ? null : RestoreWorkspaceFlow.ValidateName(_name, _existingNames);

    private string FolderPath =>
        string.IsNullOrWhiteSpace(_name) || NameError is not null ? string.Empty : WorkspaceService.GetWorkspacePath(_name.Trim(), null);

    private FolderCheck FolderState => RestoreWorkspaceFlow.EvaluateFolder(_folder);

    private string? WorkerUnavailable => WorkerBridge.GetUnavailableReason();

    private bool IsWorkerVersionMismatch => WorkerConnectionTracker.State == WorkerConnectionState.VersionMismatch;

    private bool PreflightPending => _preflightGate.IsPending;

    private bool CanRestore => RestoreWorkspaceFlow.CanRestore(new RestoreReadiness(
        _busy,
        _checking,
        WorkerUnavailable,
        _repositoryId,
        PreflightPending ? null : _preflight,
        _name,
        _existingNames,
        FolderState));

    protected override async Task OnParametersSetAsync()
    {
        if (IsVisible && !_wasVisible)
        {
            _wasVisible = true;
            await ResetAsync();
        }
        else if (!IsVisible)
        {
            _wasVisible = false;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Focus the dialog once when it opens (and when the result panel replaces the form) so Enter / Escape work;
        // focusing on every render would pull focus out of the repository picker and the name field.
        if (IsVisible && !_busy && _focusPending)
        {
            _focusPending = false;
            try
            {
                await _modalElement.FocusAsync();
            }
            catch
            {
                // circuit / JS unavailable
            }
        }
    }

    private async Task ResetAsync()
    {
        _repositoryId = null;
        _name = string.Empty;
        _autoName = string.Empty;
        _folder = null;
        _checking = false;
        CancelPreflight();
        _preflight = null;
        _error = null;
        _errorResidue = null;
        _busy = false;
        _panel = null;
        _restoredWorkspaceId = null;
        _focusPending = true;

        try
        {
            await using var db = await DbContextFactory.CreateDbContextAsync();
            var rows = await db.Repositories.AsNoTracking()
                .Where(r => r.Connector != null && r.Connector.ConnectorType == ConnectorType.GitHub)
                .Select(r => new { r.RepositoryId, r.RepositoryName, r.OrgName, ConnectorName = r.Connector!.ConnectorName })
                .ToListAsync();
            _choices = RestoreWorkspaceFlow.BuildChoices(
                rows.Select(r => new RestoreRepositorySource(r.RepositoryId, r.RepositoryName, r.OrgName, r.ConnectorName)));
            _pickerChoices = RestoreWorkspaceFlow.ToPickerChoices(_choices);

            var names = await db.Workspaces.AsNoTracking().Select(w => w.Name).ToListAsync();
            _existingNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load repositories for the restore dialog.");
            _choices = Array.Empty<RestoreRepositoryChoice>();
            _pickerChoices = Array.Empty<RepositoryPickerChoice>();
            _error = "Failed to load repositories. Please try again.";
        }
    }

    /// <summary>Selecting a repository (also the same one again, which re-checks it) starts the preflight right away.</summary>
    private async Task OnRepositoryChangedAsync(int? repositoryId)
    {
        _repositoryId = repositoryId;
        _error = null;
        _errorResidue = null;
        var choice = _choices.FirstOrDefault(c => c.RepositoryId == _repositoryId);

        var preflight = StartPreflightAsync(_repositoryId);

        // Keep following the repository name until the user types their own.
        var (name, autoName) = RestoreWorkspaceFlow.FollowRepositoryName(_name, _autoName, choice?.RepositoryName);
        var nameChanged = !string.Equals(name, _name, StringComparison.Ordinal);
        _name = name;
        _autoName = autoName;
        if (nameChanged)
            await CheckFolderAsync();

        await preflight;
    }

    private async Task StartPreflightAsync(int? repositoryId)
    {
        CancelPreflight();
        _preflight = null;
        if (repositoryId is not { } id)
            return;

        var cts = _preflightCts = new CancellationTokenSource();
        var token = _preflightGate.Begin(id);
        RestoreWorkspacePreflight result;
        try
        {
            result = await ScopedExecutor.ExecuteAsync<IWorkspaceRepositoryOperations, RestoreWorkspacePreflight>(
                operations => operations.PreflightRestoreAsync(id, cts.Token),
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Restore preflight failed for repository {RepositoryId}", id);
            result = RestoreWorkspacePreflight.Failed(ex.Message);
        }

        // A newer selection owns the dialog now; this answer is for a repository the user moved away from.
        if (!_preflightGate.TryComplete(token, _repositoryId))
            return;

        _preflight = result;
        if (!_disposed)
            await InvokeAsync(StateHasChanged);
    }

    private void CancelPreflight()
    {
        _preflightGate.Reset();
        _preflightCts?.Cancel();
        _preflightCts?.Dispose();
        _preflightCts = null;
    }

    private async Task OnNameChangedAsync(ChangeEventArgs e)
    {
        _name = e.Value?.ToString() ?? string.Empty;
        await CheckFolderAsync();
    }

    private async Task CheckFolderAsync()
    {
        _checkCts?.Cancel();
        _checkCts?.Dispose();
        _folder = null;

        var name = _name.Trim();
        if (name.Length == 0 || RestoreWorkspaceFlow.ValidateName(name, _existingNames) is not null)
        {
            _checkCts = null;
            _checking = false;
            return;
        }

        var cts = _checkCts = new CancellationTokenSource();
        var requestId = ++_checkRequestId;
        _checking = true;
        try
        {
            await Task.Delay(200, cts.Token);
            var state = await WorkspaceService.GetDirectoryStateAsync(name, null, cts.Token);
            if (requestId == _checkRequestId)
                _folder = state;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to check the folder for workspace {WorkspaceName}", name);
        }
        finally
        {
            if (requestId == _checkRequestId)
            {
                _checking = false;
                if (!_disposed)
                    await InvokeAsync(StateHasChanged);
            }
        }
    }

    private void StartRestore()
    {
        if (!CanRestore)
            return;

        var repositoryId = _repositoryId!.Value;
        var name = _name.Trim();
        var jobKey = new Uri(NavigationManager.Uri).AbsolutePath.ToLowerInvariant();
        if (JobService.IsRunning(jobKey))
            return;

        _busy = true;
        _error = null;
        _errorResidue = null;

        // The dialog is hidden while busy; the page-level BackgroundJobOverlay (keyed by this page's path) covers it.
        JobService.StartJob(jobKey, "Restoring Workspace...", async (job, ct) =>
        {
            RestoreFlowOutcome outcome;
            try
            {
                outcome = await ScopedExecutor.ExecuteAsync<IWorkspaceRepositoryOperations, RestoreFlowOutcome>(
                    operations => RestoreWorkspaceFlow.RunAsync(
                        operations,
                        repositoryId,
                        name,
                        (workspaceId, progress, token) => SyncNewWorkspaceAsync(workspaceId, progress, token),
                        job.ToOperationProgress(),
                        ct),
                    ct);
            }
            catch (OperationCanceledException)
            {
                await InvokeAsync(() =>
                {
                    _busy = false;
                    StateHasChanged();
                });
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Restore from Workspace repository failed for {WorkspaceName}", name);
                outcome = new RestoreFlowOutcome(RestoreWorkspaceResult.Failed(ex.Message), false, null);
            }

            await InvokeAsync(async () =>
            {
                if (_disposed)
                    return;
                await OnFinishedAsync(outcome);
            });
        });
    }

    private async Task SyncNewWorkspaceAsync(int workspaceId, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var contextId = await ScopedExecutor.ExecuteAsync<IWorkspaceFeatureContextResolver, WorkspaceFeatureContextId>(
            resolver => resolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, ct),
            ct);

        await ScopedExecutor.ExecuteAsync<IWorkspaceSyncOperations>(
            sync => sync.SyncAsync(
                workspaceId,
                contextId,
                repositoryIds: null,
                skipDependencyLevelPersistence: false,
                ct,
                progress,
                (_, _) => { },
                (_, _) => { }),
            ct);
    }

    private async Task OnFinishedAsync(RestoreFlowOutcome outcome)
    {
        _busy = false;
        await OnRestored.InvokeAsync();

        if (!outcome.Restore.Success || outcome.Restore.WorkspaceId is not { } workspaceId)
        {
            _error = outcome.Restore.Error ?? "Restore failed.";
            _errorResidue = outcome.Restore.CleanupResidue;

            // The failure may have been a name taken or a folder filled meanwhile; look again.
            await RefreshExistingNamesAsync();
            await CheckFolderAsync();
            StateHasChanged();
            return;
        }

        _restoredWorkspaceId = workspaceId;
        var panel = RestoreResultPanel.From(outcome);
        if (!panel.HasContent)
        {
            OpenWorkspace();
            return;
        }

        _panel = panel;
        _focusPending = true;
        StateHasChanged();
    }

    private async Task RefreshExistingNamesAsync()
    {
        try
        {
            await using var db = await DbContextFactory.CreateDbContextAsync();
            var names = await db.Workspaces.AsNoTracking().Select(w => w.Name).ToListAsync();
            _existingNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to refresh Workspace names for the restore dialog.");
        }
    }

    private void OpenWorkspace()
    {
        if (_restoredWorkspaceId is not { } workspaceId)
            return;

        NavigationManager.NavigateTo($"workspaces/{workspaceId}");
    }

    private void OpenConnectors() => NavigationManager.NavigateTo("connectors");

    private void OpenWorkerPage() => NavigationManager.NavigateTo("worker");

    private async Task CloseAsync()
    {
        if (_panel is not null)
            OpenWorkspace();
        else
            await CancelAsync();
    }

    private async Task CancelAsync()
    {
        if (!_busy)
            await OnCancel.InvokeAsync();
    }

    private async Task HandleKeyDownAsync(KeyboardEventArgs e)
    {
        if (_busy)
            return;

        if (e.Key == "Escape")
        {
            if (_panel is not null)
                OpenWorkspace();
            else
                await CancelAsync();
        }
        else if (ModalKeyboard.IsPlainEnter(e))
        {
            if (_panel is not null)
                OpenWorkspace();
            else
                StartRestore();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _checkCts?.Cancel();
        _checkCts?.Dispose();
        CancelPreflight();
    }
}

/// <summary>Folder verdict for a new Workspace: <c>Error</c> blocks Restore, <c>Note</c> is informational.</summary>
public sealed record FolderCheck(string? Error, string? Note);

/// <summary>One imported repository as the dialog lists it.</summary>
public sealed record RestoreRepositorySource(int RepositoryId, string RepositoryName, string? OrgName, string? ConnectorName);

/// <summary>One repository the dialog offers: "org/repository", with the connector when that alone is ambiguous.</summary>
public sealed record RestoreRepositoryChoice(int RepositoryId, string RepositoryName, string DisplayName);

/// <summary>Everything that decides whether Restore is enabled.</summary>
public sealed record RestoreReadiness(
    bool Busy,
    bool CheckingFolder,
    string? WorkerUnavailable,
    int? RepositoryId,
    RestoreWorkspacePreflight? Preflight,
    string Name,
    IEnumerable<string> ExistingNames,
    FolderCheck Folder);

/// <summary>Outcome of the restore flow: the restore result plus whether the follow-up Sync was requested and how it ended.</summary>
public sealed record RestoreFlowOutcome(RestoreWorkspaceResult Restore, bool SyncRequested, string? SyncError);

/// <summary>
/// Makes sure a preflight answer only lands for the repository that is still selected: every selection takes a new
/// token, and an answer carrying an older token (or arriving after the selection changed) is dropped.
/// </summary>
public sealed class RestorePreflightGate
{
    private int _token;
    private int? _pendingRepositoryId;

    public bool IsPending => _pendingRepositoryId is not null;

    public int Begin(int repositoryId)
    {
        _pendingRepositoryId = repositoryId;
        return ++_token;
    }

    /// <summary>True when the answer for <paramref name="token"/> is still wanted; it then stops being pending.</summary>
    public bool TryComplete(int token, int? selectedRepositoryId)
    {
        if (token != _token || _pendingRepositoryId is not { } pending || pending != selectedRepositoryId)
            return false;

        _pendingRepositoryId = null;
        return true;
    }

    public void Reset()
    {
        _token++;
        _pendingRepositoryId = null;
    }
}

/// <summary>What the result panel shows after a restore that needs the user's attention.</summary>
public sealed record RestoreResultPanel(
    string? SyncError,
    string? Warning,
    IReadOnlyList<string> UnresolvedConnectors,
    IReadOnlyList<string> UnresolvedRepositories)
{
    public const string ImportGuidance =
        "Import them through Connectors, then add them from the Workspace's repository list.";

    public const string SyncRetryHint = "You can open the Workspace and retry Sync.";

    public bool HasContent =>
        SyncError is not null || Warning is not null || UnresolvedConnectors.Count > 0 || UnresolvedRepositories.Count > 0;

    public bool HasUnresolved => UnresolvedConnectors.Count > 0 || UnresolvedRepositories.Count > 0;

    public string Headline => SyncError is not null
        ? "Workspace restored, but the initial Sync did not complete."
        : "Workspace restored";

    /// <summary>"2 repositories are not available on this computer." / "1 connector must be configured."</summary>
    public IReadOnlyList<string> SummaryLines
    {
        get
        {
            var lines = new List<string>();
            if (UnresolvedRepositories.Count > 0)
            {
                lines.Add(UnresolvedRepositories.Count == 1
                    ? "1 repository is not available on this computer."
                    : $"{UnresolvedRepositories.Count} repositories are not available on this computer.");
            }

            if (UnresolvedConnectors.Count > 0)
            {
                lines.Add(UnresolvedConnectors.Count == 1
                    ? "1 connector must be configured."
                    : $"{UnresolvedConnectors.Count} connectors must be configured.");
            }

            return lines;
        }
    }

    public static RestoreResultPanel From(RestoreFlowOutcome outcome) =>
        new(
            string.IsNullOrWhiteSpace(outcome.SyncError) ? null : outcome.SyncError,
            string.IsNullOrWhiteSpace(outcome.Restore.Warning) ? null : outcome.Restore.Warning,
            outcome.Restore.UnresolvedConnectors,
            outcome.Restore.UnresolvedRepositories);
}

/// <summary>Pure and service-level pieces of the restore dialog, kept out of the component so they can be tested.</summary>
public static class RestoreWorkspaceFlow
{
    public const string EmptyFolderNote = "The folder already exists and is empty. GrayMoon will use it.";

    public const string NonEmptyFolderError = "This folder already contains files. Choose another Workspace name or move the existing files.";

    public const string UnknownFolderNote = "The folder already exists. Restore continues only if it is empty.";

    /// <summary>"org/name" for every repository; when two entries would read the same, the connector is added.</summary>
    public static IReadOnlyList<RestoreRepositoryChoice> BuildChoices(IEnumerable<RestoreRepositorySource> repositories)
    {
        var rows = repositories
            .Select(r => (Source: r, Display: string.IsNullOrWhiteSpace(r.OrgName) ? r.RepositoryName : $"{r.OrgName}/{r.RepositoryName}"))
            .ToList();
        var duplicates = rows
            .GroupBy(r => r.Display, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return rows
            .Select(r => new RestoreRepositoryChoice(
                r.Source.RepositoryId,
                r.Source.RepositoryName,
                duplicates.Contains(r.Display) && !string.IsNullOrWhiteSpace(r.Source.ConnectorName)
                    ? $"{r.Display} ({r.Source.ConnectorName})"
                    : r.Display))
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.RepositoryId)
            .ToList();
    }

    /// <summary>The same rows for the shared repository picker (filtered in memory there, without a "None" row).</summary>
    public static IReadOnlyList<RepositoryPickerChoice> ToPickerChoices(IEnumerable<RestoreRepositoryChoice> choices) =>
        choices.Select(c => new RepositoryPickerChoice(c.RepositoryId, c.DisplayName)).ToList();

    /// <summary>Default Workspace name for a repository: its name without the organisation prefix of "org/name".</summary>
    public static string DefaultWorkspaceName(string? repositoryDisplayName)
    {
        if (string.IsNullOrWhiteSpace(repositoryDisplayName))
            return string.Empty;

        var trimmed = repositoryDisplayName.Trim();
        var slash = trimmed.LastIndexOf('/');
        return (slash >= 0 ? trimmed[(slash + 1)..] : trimmed).Trim();
    }

    /// <summary>
    /// The name follows the selected repository until the user types their own; a typed name is never replaced.
    /// Returns the new name and the new automatic name.
    /// </summary>
    public static (string Name, string AutoName) FollowRepositoryName(string currentName, string currentAutoName, string? repositoryName)
    {
        if (!string.IsNullOrWhiteSpace(currentName) && !string.Equals(currentName, currentAutoName, StringComparison.Ordinal))
            return (currentName, currentAutoName);

        var autoName = DefaultWorkspaceName(repositoryName);
        return (autoName, autoName);
    }

    /// <summary>Null when the name is usable: required, a valid folder name and not used by another Workspace.</summary>
    public static string? ValidateName(string? name, IEnumerable<string> existingWorkspaceNames)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return "Workspace name is required.";

        if (trimmed is "." or ".." || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "Workspace name contains characters that cannot be used in a folder name.";

        if (existingWorkspaceNames.Any(n => string.Equals(n?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
            return "Workspace name already exists.";

        return null;
    }

    /// <summary>
    /// The folder must be absent or empty. An existing empty folder is used (informational note); an existing folder
    /// with anything in it blocks Restore. Restore never merges a Workspace repository into an arbitrary directory.
    /// </summary>
    public static FolderCheck EvaluateFolder(WorkspaceDirectoryState? state)
    {
        if (state is null || !state.Exists)
            return new FolderCheck(null, null);

        return state.IsEmpty switch
        {
            true => new FolderCheck(null, EmptyFolderNote),
            false => new FolderCheck(NonEmptyFolderError, null),
            null => new FolderCheck(null, UnknownFolderNote),
        };
    }

    public static bool CanRestore(RestoreReadiness r) =>
        !r.Busy
        && !r.CheckingFolder
        && r.WorkerUnavailable is null
        && r.RepositoryId is not null
        && r.Preflight is { Success: true }
        && !string.IsNullOrWhiteSpace(r.Name)
        && ValidateName(r.Name, r.ExistingNames) is null
        && r.Folder.Error is null;

    public static string ProfileTypeLabel(WorkspaceType? type) => type switch
    {
        WorkspaceType.DotNetDependency => ".NET Dependency",
        _ => "Basic",
    };

    public static string VersioningLabel(WorkspaceVersioningMode? mode) => mode switch
    {
        WorkspaceVersioningMode.GitVersion => "GitVersion",
        _ => "No versioning",
    };

    public static string CiLabel(WorkspaceCiProvider? ci) => ci switch
    {
        WorkspaceCiProvider.GitHubActions => "GitHub Actions",
        _ => "No CI",
    };

    /// <summary>The pre-restore summary of what will be missing, or null when everything resolves.</summary>
    public static string? MissingSummary(RestoreWorkspacePreflight preflight)
    {
        var parts = new List<string>();
        var repositories = preflight.MissingRepositories.Count;
        var connectors = preflight.MissingConnectors.Count;
        if (repositories > 0)
        {
            parts.Add(repositories == 1
                ? "1 repository is not currently available in GrayMoon."
                : $"{repositories} repositories are not currently available in GrayMoon.");
        }

        if (connectors > 0)
        {
            parts.Add(connectors == 1
                ? "1 connector is not configured on this computer."
                : $"{connectors} connectors are not configured on this computer.");
        }

        if (parts.Count == 0)
            return null;

        parts.Add("The Workspace can still be restored.");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Restores the Workspace and, only after a successful restore, requests the normal full Sync for it
    /// (D14 step 8). A Sync that fails or is cancelled does not undo the restore; it is reported in the outcome.
    /// </summary>
    public static async Task<RestoreFlowOutcome> RunAsync(
        IWorkspaceRepositoryOperations operations,
        int repositoryId,
        string workspaceName,
        Func<int, IProgress<OperationProgress>?, CancellationToken, Task> requestSync,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var restore = await operations.RestoreFromRepositoryAsync(repositoryId, workspaceName.Trim(), progress, cancellationToken);
        if (!restore.Success || restore.WorkspaceId is not { } workspaceId)
            return new RestoreFlowOutcome(restore, false, null);

        try
        {
            progress.Report("Syncing repositories...");
            await requestSync(workspaceId, progress, cancellationToken);
            progress.Report("Finishing...");
            return new RestoreFlowOutcome(restore, true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RestoreFlowOutcome(restore, true, "Sync was cancelled.");
        }
        catch (Exception ex)
        {
            return new RestoreFlowOutcome(restore, true, ex.Message);
        }
    }
}
