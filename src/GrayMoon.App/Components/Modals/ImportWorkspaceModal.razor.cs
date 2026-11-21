using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Components.Shared;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Ui;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace GrayMoon.App.Components.Modals;

/// <summary>
/// Import from disk: pick a folder under the workspace root, see what an import would do, import it. A path of its
/// own, apart from Add Workspace (new, empty Workspace) and Restore (clone from a repository). The flow rules are in
/// <see cref="ImportWorkspaceFlow"/> so they can be tested without rendering.
/// </summary>
public sealed partial class ImportWorkspaceModal : ComponentBase, IDisposable
{
    public const string DialogCssClass = RestoreWorkspaceModal.DialogCssClass;

    [Parameter] public bool IsVisible { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }

    /// <summary>Raised after an import finished (success or not) so the host can refresh its list.</summary>
    [Parameter] public EventCallback OnImported { get; set; }

    [Inject] private WorkspaceService WorkspaceService { get; set; } = default!;
    [Inject] private IWorkerBridge WorkerBridge { get; set; } = default!;
    [Inject] private WorkerConnectionTracker WorkerConnectionTracker { get; set; } = default!;
    [Inject] private IBackgroundJobService JobService { get; set; } = default!;
    [Inject] private IScopedServiceExecutor ScopedExecutor { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<ImportWorkspaceModal> Logger { get; set; } = default!;

    private ElementReference _modalElement;
    private IReadOnlyList<string> _folders = [];
    private IReadOnlyList<RepositoryPickerChoice> _folderChoices = [];
    private string? _foldersError;
    private bool _foldersLoading;
    private string? _folder;
    private ImportWorkspacePreflight? _preflight;
    private bool _preflightPending;
    private int _preflightGeneration;
    private CancellationTokenSource? _preflightCts;
    private WorkspaceType _type = WorkspaceType.DotNetDependency;
    private bool _useGitVersion = true;
    private bool _useGitHubActions = true;
    private string? _error;
    private string? _errorResidue;
    private bool _busy;
    private bool _wasVisible;
    private bool _focusPending;
    private bool _disposed;
    private ImportResultPanel? _panel;
    private int? _importedWorkspaceId;

    private string? WorkerUnavailable => WorkerBridge.GetUnavailableReason();

    private bool IsWorkerVersionMismatch => WorkerConnectionTracker.State == WorkerConnectionState.VersionMismatch;

    private int? SelectedFolderIndex => _folder is null ? null : IndexOf(_folders, _folder);

    private string FolderPath => _folder is null ? string.Empty : WorkspaceService.GetWorkspacePath(_folder, null);

    private bool CanImport => ImportWorkspaceFlow.CanImport(_busy, WorkerUnavailable, _folder, _preflight, _preflightPending);

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
        // Focus once when the dialog opens (and when the result panel replaces the form) so Enter / Escape work.
        if (IsVisible && !_busy && _focusPending)
        {
            _focusPending = false;
            try
            {
                await _modalElement.FocusAsync();
            }
            catch (Exception ex) when (ex is Microsoft.JSInterop.JSDisconnectedException or InvalidOperationException)
            {
            }
        }
    }

    private async Task ResetAsync()
    {
        CancelPreflight();
        _folder = null;
        _preflight = null;
        _preflightPending = false;
        _error = null;
        _errorResidue = null;
        _panel = null;
        _importedWorkspaceId = null;
        _type = WorkspaceType.DotNetDependency;
        _useGitVersion = true;
        _useGitHubActions = true;
        _focusPending = true;
        await LoadFoldersAsync();
    }

    private async Task LoadFoldersAsync()
    {
        _foldersLoading = true;
        _foldersError = null;
        try
        {
            await WorkspaceService.GetRootPathAsync();
            var list = await ScopedExecutor.ExecuteAsync<IWorkspaceImportOperations, ImportableFolderList>(
                operations => operations.ListImportableFoldersAsync(CancellationToken.None), CancellationToken.None);
            if (_disposed)
                return;

            _folders = list.Folders;
            _folderChoices = list.Folders.Select((name, index) => new RepositoryPickerChoice(index, name)).ToList();
            _foldersError = list.Error;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not list the folders under the workspace root.");
            _folders = [];
            _folderChoices = [];
            _foldersError = "The folders under the workspace root could not be listed. Try again.";
        }
        finally
        {
            _foldersLoading = false;
        }
    }

    private async Task OnFolderPickedAsync(int? index)
    {
        _folder = index is int i && i >= 0 && i < _folders.Count ? _folders[i] : null;
        _error = null;
        _errorResidue = null;
        await RunPreflightAsync();
    }

    private async Task RunPreflightAsync()
    {
        CancelPreflight();
        _preflight = null;
        var folder = _folder;
        if (folder is null)
        {
            _preflightPending = false;
            StateHasChanged();
            return;
        }

        var generation = ++_preflightGeneration;
        var cts = new CancellationTokenSource();
        _preflightCts = cts;
        _preflightPending = true;
        StateHasChanged();

        ImportWorkspacePreflight result;
        try
        {
            result = await ScopedExecutor.ExecuteAsync<IWorkspaceImportOperations, ImportWorkspacePreflight>(
                operations => operations.PreflightImportAsync(folder, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Import preflight failed for {Folder}", folder);
            result = ImportWorkspacePreflight.Failed("The folder could not be checked. Try again.");
        }

        // Only the answer for the folder that is still selected is shown.
        if (_disposed || generation != _preflightGeneration)
            return;

        _preflight = result;
        _preflightPending = false;
        if (result is { Success: true, ProfileFromDefinition: false })
        {
            var defaults = WorkspaceProfileDefaults.ForType(_type);
            _useGitVersion = defaults.Versioning == WorkspaceVersioningMode.GitVersion;
            _useGitHubActions = defaults.Ci == WorkspaceCiProvider.GitHubActions;
        }

        StateHasChanged();
    }

    private void CancelPreflight()
    {
        _preflightGeneration++;
        var cts = _preflightCts;
        _preflightCts = null;
        if (cts is null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    private void OnTypeChosen(WorkspaceType type)
    {
        _type = type;
        var defaults = WorkspaceProfileDefaults.ForType(type);
        _useGitVersion = defaults.Versioning == WorkspaceVersioningMode.GitVersion;
        _useGitHubActions = defaults.Ci == WorkspaceCiProvider.GitHubActions;
    }

    private void StartImport()
    {
        if (!CanImport)
            return;

        var folder = _folder!;
        var options = new ImportWorkspaceOptions(
            _type,
            _useGitVersion ? WorkspaceVersioningMode.GitVersion : WorkspaceVersioningMode.None,
            _useGitHubActions ? WorkspaceCiProvider.GitHubActions : WorkspaceCiProvider.None);
        var jobKey = new Uri(NavigationManager.Uri).AbsolutePath.ToLowerInvariant();
        if (JobService.IsRunning(jobKey))
            return;

        _busy = true;
        _error = null;
        _errorResidue = null;

        // The dialog is hidden while busy; the page-level BackgroundJobOverlay (keyed by this page's path) covers it.
        JobService.StartJob(jobKey, "Importing Workspace...", async (job, ct) =>
        {
            ImportFlowOutcome outcome;
            try
            {
                outcome = await ScopedExecutor.ExecuteAsync<IWorkspaceImportOperations, ImportFlowOutcome>(
                    operations => ImportWorkspaceFlow.RunAsync(
                        operations,
                        folder,
                        options,
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
                Logger.LogError(ex, "Import from disk failed for {Folder}", folder);
                outcome = new ImportFlowOutcome(ImportWorkspaceResult.Failed(ex.Message), false, null);
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

    private async Task OnFinishedAsync(ImportFlowOutcome outcome)
    {
        _busy = false;
        await OnImported.InvokeAsync();

        if (!outcome.Import.Success || outcome.Import.WorkspaceId is not { } workspaceId)
        {
            _error = outcome.Import.Error ?? "The import failed.";
            _errorResidue = outcome.Import.RollbackResidue;

            // The failure may have been a name taken or a folder changed meanwhile; look again.
            await LoadFoldersAsync();
            if (_folder is not null && IndexOf(_folders, _folder) is null)
                _folder = null;
            await RunPreflightAsync();
            return;
        }

        _importedWorkspaceId = workspaceId;
        var panel = ImportResultPanel.From(outcome);
        if (!panel.HasContent)
        {
            OpenWorkspace();
            return;
        }

        _panel = panel;
        _focusPending = true;
        StateHasChanged();
    }

    private void OpenWorkspace()
    {
        if (_importedWorkspaceId is { } workspaceId)
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
            await CloseAsync();
        }
        else if (ModalKeyboard.IsPlainEnter(e))
        {
            if (_panel is not null)
                OpenWorkspace();
            else
                StartImport();
        }
    }

    private static int? IndexOf(IReadOnlyList<string> folders, string folder)
    {
        for (var i = 0; i < folders.Count; i++)
        {
            if (string.Equals(folders[i], folder, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return null;
    }

    public void Dispose()
    {
        _disposed = true;
        CancelPreflight();
    }
}

/// <summary>What an import run came to: the import itself, and the Sync that follows a successful one.</summary>
public sealed record ImportFlowOutcome(ImportWorkspaceResult Import, bool SyncRequested, string? SyncError);

/// <summary>What the dialog shows after a successful import that has something to tell.</summary>
public sealed record ImportResultPanel(
    string Headline,
    string? SyncError,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> UnresolvedRepositories,
    IReadOnlyList<string> UnresolvedConnectors)
{
    public const string SyncRetryHint = "The Workspace was imported, but the first Sync did not finish. Open the Workspace and press Sync.";

    public const string ImportGuidance = "Import them through Connectors, then add them to the Workspace.";

    public bool HasUnresolved => UnresolvedRepositories.Count > 0 || UnresolvedConnectors.Count > 0;

    /// <summary>False when the import and its Sync went through with nothing to report: the dialog then just opens the Workspace.</summary>
    public bool HasContent => SyncError is not null || Warnings.Count > 0 || HasUnresolved;

    public static ImportResultPanel From(ImportFlowOutcome outcome) => new(
        outcome.Import.UnresolvedRepositories.Count > 0 || outcome.Import.UnresolvedConnectors.Count > 0
            ? "Workspace imported. Some repositories of its definition are not available on this computer."
            : "Workspace imported.",
        outcome.SyncError,
        outcome.Import.Warnings,
        outcome.Import.UnresolvedRepositories,
        outcome.Import.UnresolvedConnectors);
}

/// <summary>The rules of the Import dialog, kept apart from the component so they are tested directly.</summary>
public static class ImportWorkspaceFlow
{
    public const string CheckingFolderStatus = "Checking folder...";

    public const string NoFoldersMessage = "There is no folder under the workspace root that is not a workspace already.";

    /// <summary>Import is possible only for a folder whose check succeeded and is still current, with a usable Worker.</summary>
    public static bool CanImport(bool busy, string? workerUnavailable, string? folder, ImportWorkspacePreflight? preflight, bool preflightPending) =>
        !busy
        && workerUnavailable is null
        && !string.IsNullOrWhiteSpace(folder)
        && !preflightPending
        && preflight is { Success: true };

    public static string ShapeLabel(ImportFolderShape shape) => shape switch
    {
        ImportFolderShape.WorkspaceRepository => "Workspace repository with a definition",
        ImportFolderShape.RepositoryWithoutDefinition => "Git repository without a definition",
        _ => "Folder with repositories",
    };

    public static string CountLabel(int count) => count == 1 ? "1 repository" : $"{count} repositories";

    /// <summary>
    /// Imports the folder and, only after a successful import, requests the normal full Sync for the new Workspace
    /// (it clones what is missing and installs the Git hooks). A Sync that fails or is cancelled does not undo the
    /// import; it is reported in the outcome.
    /// </summary>
    public static async Task<ImportFlowOutcome> RunAsync(
        IWorkspaceImportOperations operations,
        string folderName,
        ImportWorkspaceOptions options,
        Func<int, IProgress<OperationProgress>?, CancellationToken, Task> requestSync,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var import = await operations.ImportFromDiskAsync(folderName, options, progress, cancellationToken);
        if (!import.Success || import.WorkspaceId is not { } workspaceId)
            return new ImportFlowOutcome(import, false, null);

        try
        {
            progress.Report("Syncing repositories...");
            await requestSync(workspaceId, progress, cancellationToken);
            progress.Report("Finishing...");
            return new ImportFlowOutcome(import, true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ImportFlowOutcome(import, true, "Sync was cancelled.");
        }
        catch (Exception ex)
        {
            return new ImportFlowOutcome(import, true, ex.Message);
        }
    }
}
