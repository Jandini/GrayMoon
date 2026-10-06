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
/// "Restore from repository" dialog (D14): pick an imported GitHub repository and a Workspace name, restore the
/// Workspace as a background job under the page overlay, run the normal full Sync, then open the new Workspace.
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
    [Inject] private IBackgroundJobService JobService { get; set; } = default!;
    [Inject] private IScopedServiceExecutor ScopedExecutor { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<RestoreWorkspaceModal> Logger { get; set; } = default!;

    private ElementReference _modalElement;
    private IReadOnlyList<WorkspaceModal.WorkspaceRepositoryChoice> _choices = Array.Empty<WorkspaceModal.WorkspaceRepositoryChoice>();
    private HashSet<string> _existingNames = new(StringComparer.OrdinalIgnoreCase);
    private int? _repositoryId;
    private string _filter = string.Empty;
    private string _name = string.Empty;
    private string _autoName = string.Empty;
    private bool _directoryExists;
    private int _directoryRepositoryCount;
    private bool _checking;
    private int _checkRequestId;
    private CancellationTokenSource? _checkCts;
    private string? _error;
    private bool _busy;
    private bool _wasVisible;
    private bool _disposed;
    private RestoreResultPanel? _panel;
    private int? _restoredWorkspaceId;

    private IReadOnlyList<WorkspaceModal.WorkspaceRepositoryChoice> FilteredChoices =>
        WorkspaceModal.FilterWorkspaceRepositoryChoices(_choices, _filter);

    private string? NameError => string.IsNullOrWhiteSpace(_name) ? null : RestoreWorkspaceFlow.ValidateName(_name, _existingNames);

    private FolderCheck FolderState => RestoreWorkspaceFlow.EvaluateFolder(
        _directoryExists,
        _directoryRepositoryCount,
        string.IsNullOrWhiteSpace(_name) ? string.Empty : WorkspaceService.GetWorkspacePath(_name.Trim(), null));

    private bool CanRestore =>
        !_busy
        && !_checking
        && WorkerBridge.IsWorkerConnected
        && _repositoryId is not null
        && !string.IsNullOrWhiteSpace(_name)
        && RestoreWorkspaceFlow.ValidateName(_name, _existingNames) is null
        && FolderState.Error is null;

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
        if (IsVisible && !_busy && _panel is null)
        {
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
        _filter = string.Empty;
        _name = string.Empty;
        _autoName = string.Empty;
        _directoryExists = false;
        _directoryRepositoryCount = 0;
        _checking = false;
        _error = null;
        _busy = false;
        _panel = null;
        _restoredWorkspaceId = null;

        try
        {
            await using var db = await DbContextFactory.CreateDbContextAsync();
            var rows = await db.Repositories.AsNoTracking()
                .Where(r => r.Connector != null && r.Connector.ConnectorType == ConnectorType.GitHub)
                .Select(r => new { r.RepositoryId, r.RepositoryName, r.OrgName })
                .ToListAsync();
            _choices = WorkspaceModal.BuildWorkspaceRepositoryChoices(
                rows.Select(r => (r.RepositoryId, r.RepositoryName, r.OrgName)),
                new HashSet<int>());

            var names = await db.Workspaces.AsNoTracking().Select(w => w.Name).ToListAsync();
            _existingNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load repositories for the restore dialog.");
            _choices = Array.Empty<WorkspaceModal.WorkspaceRepositoryChoice>();
            _error = "Failed to load repositories. Please try again.";
        }

        if (!WorkerBridge.IsWorkerConnected)
            _error = "Worker is not available. Please start the GrayMoon Worker to restore a workspace.";
    }

    private void OnFilterChanged(ChangeEventArgs e) => _filter = e.Value?.ToString() ?? string.Empty;

    private async Task OnRepositoryChangedAsync(ChangeEventArgs e)
    {
        _repositoryId = int.TryParse(e.Value?.ToString(), out var id) ? id : null;
        var choice = _choices.FirstOrDefault(c => c.RepositoryId == _repositoryId);

        // Keep following the repository name until the user types their own.
        if (string.IsNullOrWhiteSpace(_name) || string.Equals(_name, _autoName, StringComparison.Ordinal))
        {
            _autoName = RestoreWorkspaceFlow.DefaultWorkspaceName(choice?.DisplayName);
            _name = _autoName;
            await CheckFolderAsync();
        }
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
        _directoryExists = false;
        _directoryRepositoryCount = 0;

        var name = _name.Trim();
        if (name.Length == 0 || RestoreWorkspaceFlow.ValidateName(name, _existingNames) is not null)
        {
            _checking = false;
            return;
        }

        var cts = _checkCts = new CancellationTokenSource();
        var requestId = ++_checkRequestId;
        _checking = true;
        try
        {
            await Task.Delay(200, cts.Token);
            var exists = await WorkspaceService.DirectoryExistsAsync(name, null, cts.Token);
            var count = exists ? await WorkspaceService.GetRepositoryCountAsync(name, null, cts.Token) : 0;
            if (requestId == _checkRequestId)
            {
                _directoryExists = exists;
                _directoryRepositoryCount = count;
            }
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
                outcome = new RestoreFlowOutcome(RestoreWorkspaceFlow.Failure(ex.Message), false, null);
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
        StateHasChanged();
    }

    private void OpenWorkspace()
    {
        if (_restoredWorkspaceId is not { } workspaceId)
            return;

        NavigationManager.NavigateTo($"workspaces/{workspaceId}");
    }

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
    }
}

/// <summary>Folder verdict for a new Workspace: <c>Error</c> blocks Restore, <c>Note</c> is informational.</summary>
public sealed record FolderCheck(string? Error, string? Note);

/// <summary>Outcome of the restore flow: the restore result plus whether the follow-up Sync was requested and how it ended.</summary>
public sealed record RestoreFlowOutcome(RestoreWorkspaceResult Restore, bool SyncRequested, string? SyncError);

/// <summary>What the result panel shows after a restore: warnings plus what could not be resolved.</summary>
public sealed record RestoreResultPanel(
    string? Warning,
    IReadOnlyList<string> UnresolvedConnectors,
    IReadOnlyList<string> UnresolvedRepositories)
{
    public const string ImportGuidance = "Import these repositories through their connector, then use Review on the Repositories page.";

    public bool HasContent => Warning is not null || UnresolvedConnectors.Count > 0 || UnresolvedRepositories.Count > 0;

    public bool ShowImportGuidance => UnresolvedConnectors.Count > 0 || UnresolvedRepositories.Count > 0;

    public static RestoreResultPanel From(RestoreFlowOutcome outcome)
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(outcome.Restore.Error))
            warnings.Add(outcome.Restore.Error!);
        if (!string.IsNullOrWhiteSpace(outcome.SyncError))
            warnings.Add($"Sync did not finish: {outcome.SyncError} Use Sync on the Repositories page to retry.");

        return new RestoreResultPanel(
            warnings.Count == 0 ? null : string.Join(" ", warnings),
            outcome.Restore.UnresolvedConnectorUrls,
            outcome.Restore.UnresolvedRepositoryUrls);
    }
}

/// <summary>Pure and service-level pieces of the restore dialog, kept out of the component so they can be tested.</summary>
public static class RestoreWorkspaceFlow
{
    /// <summary>Default Workspace name for a repository: its name without the organisation prefix of "org/name".</summary>
    public static string DefaultWorkspaceName(string? repositoryDisplayName)
    {
        if (string.IsNullOrWhiteSpace(repositoryDisplayName))
            return string.Empty;

        var trimmed = repositoryDisplayName.Trim();
        var slash = trimmed.LastIndexOf('/');
        return (slash >= 0 ? trimmed[(slash + 1)..] : trimmed).Trim();
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
    /// The folder must be absent or empty. The App can only see whether the folder exists and how many repositories
    /// it holds, so an existing folder with repositories is refused here; any other existing folder is allowed with a
    /// note, and the Worker (requireEmptyRoot) makes the final call.
    /// </summary>
    public static FolderCheck EvaluateFolder(bool directoryExists, int repositoryCount, string folderPath)
    {
        if (!directoryExists)
            return new FolderCheck(null, null);

        if (repositoryCount > 0)
        {
            return new FolderCheck(
                $"The folder {folderPath} already exists and is not empty. Choose another name or empty the folder first.",
                null);
        }

        return new FolderCheck(null, $"The folder {folderPath} already exists. Restore continues only if it is empty.");
    }

    public static RestoreWorkspaceResult Failure(string error) => new(false, null, error, [], []);

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
            progress.Report("Synchronizing...");
            await requestSync(workspaceId, progress, cancellationToken);
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
