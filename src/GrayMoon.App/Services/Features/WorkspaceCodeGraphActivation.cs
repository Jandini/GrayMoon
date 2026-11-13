using GrayMoon.App.Services.WorkspaceManifest;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Application.WorkspaceManifest;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// Saves the workspace-wide CodeGraph switch into <c>graymoon.json</c> and, when it is on, starts an index for the
/// Workspace folder. The App does not look in <c>.codegraph</c> itself: the Worker says whether a build is needed.
/// An index that is already there does not open the overlay. Unchecking the switch writes <c>false</c> and does not
/// delete an index. The files and the index are those of the context the settings were opened from: the special
/// Workspace from the Workspaces list, a Feature's own folder from that Feature.
/// </summary>
public sealed class WorkspaceCodeGraphActivation(
    IWorkspaceManifestService workspaceManifest,
    FeatureCodeGraphService codeGraph,
    IWorkspaceOperationLock operations,
    ILogger<WorkspaceCodeGraphActivation> logger)
{
    public const string OperationKind = "codegraph-init";

    private int _activeWorkspaceId;

    /// <summary>
    /// The Workspace whose index this circuit is starting or watching, or null. The Workspaces list has no
    /// Workspace id in its path, so its overlay finds the running operation through this, for a Workspace that
    /// was just added as well as for one that is edited.
    /// </summary>
    public int? ActiveWorkspaceId => Volatile.Read(ref _activeWorkspaceId) is > 0 and var id ? id : null;

    /// <summary>
    /// Use CodeGraph is stored in the Workspace definition. Add Workspace asks this before it creates anything,
    /// so a ticked box with no Workspace repository is refused while nothing has been created yet.
    /// </summary>
    public static string? MissingWorkspaceRepositoryMessage(bool offered, bool selected, int? workspaceRepositoryId) =>
        offered && selected && workspaceRepositoryId is null
            ? WorkspaceManifestService.CodeGraphNeedsWorkspaceRepositoryMessage
            : null;

    /// <summary>
    /// An absent switch left unchecked is not a request to write <c>false</c>. Checking it, or saving a switch the
    /// definition already sets, writes the checkbox.
    /// </summary>
    public static bool ShouldWrite(bool? storedSwitch, bool selected) =>
        selected || storedSwitch is not null;

    /// <summary>
    /// Writes the switch when <see cref="ShouldWrite"/> says so, syncs <c>.gitignore</c> and <c>codegraph.json</c>,
    /// then starts the Workspace index when the switch is on. Returns the message to show, or null when the save
    /// can close. A missing Workspace repository fails in the open: the flag is not pretended to be stored.
    /// </summary>
    public async Task<string?> SaveAsync(
        int workspaceId,
        bool? storedSwitch,
        bool selected,
        string overlayKey,
        WorkspaceFeatureContextId? contextId = null,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldWrite(storedSwitch, selected))
            return null;

        // Opened from a context: that context's definition, .gitignore, codegraph.json and index. Opened from the
        // Workspaces list (null): the special Workspace's.
        var manifest = contextId is { } target ? workspaceManifest.ForContext(target) : workspaceManifest;

        var write = await manifest.SetCodeGraphEnabledAsync(workspaceId, selected, cancellationToken);
        if (!write.Success && write.Error == WorkspaceManifestService.DefinitionNotFoundMessage)
        {
            var created = await manifest.SyncDefinitionToDiskAsync(workspaceId, cancellationToken);
            if (created is not null)
                return created;

            write = await manifest.SetCodeGraphEnabledAsync(workspaceId, selected, cancellationToken);
        }

        if (!write.Success)
            return write.Error ?? WorkspaceManifestService.CodeGraphNeedsWorkspaceRepositoryMessage;

        var synced = await manifest.SyncDefinitionToDiskAsync(workspaceId, cancellationToken);
        if (synced is not null)
            return synced;

        if (!selected)
            return null;

        return await StartIndexAsync(workspaceId, overlayKey, contextId, cancellationToken);
    }

    /// <summary>
    /// Sends <c>InitCodeGraph</c> for the Workspace folder. <see cref="FeatureCodeGraphService.AlreadyInitialized"/>
    /// returns without a watch. <see cref="FeatureCodeGraphService.Started"/> and
    /// <see cref="FeatureCodeGraphService.AlreadyRunning"/> are watched under the structural lock, and the overlay
    /// offers Background. Any other outcome is a failure.
    /// </summary>
    public async Task<string?> StartIndexAsync(
        int workspaceId,
        string overlayKey,
        WorkspaceFeatureContextId? contextId = null,
        CancellationToken cancellationToken = default)
    {
        string outcome;
        try
        {
            outcome = await codeGraph.InitializeWorkspaceAsync(workspaceId, contextId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CodeGraph init failed. WorkspaceId={WorkspaceId}", workspaceId);
            return ex.Message;
        }

        if (!FeatureCodeGraphService.ShouldWatch(outcome))
            return outcome == FeatureCodeGraphService.AlreadyInitialized ? null : Describe(outcome);

        Volatile.Write(ref _activeWorkspaceId, workspaceId);
        try
        {
            return await WatchIndexAsync(workspaceId, overlayKey, contextId, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _activeWorkspaceId, 0);
        }
    }

    private async Task<string?> WatchIndexAsync(
        int workspaceId,
        string overlayKey,
        WorkspaceFeatureContextId? contextId,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = operations.TryStartStructural(
            workspaceId,
            OperationKind,
            overlayKey,
            "Building the CodeGraph index...",
            async (op, ct) =>
            {
                try
                {
                    string? watched = null;
                    var backgrounded = false;
                    await op.RunBackgroundableAsync(async background =>
                    {
                        using var watch = CancellationTokenSource.CreateLinkedTokenSource(background, ct);
                        try
                        {
                            watched = await codeGraph.WatchWorkspaceAsync(workspaceId, watch.Token, targetContextId: contextId);
                        }
                        catch (OperationCanceledException) when (background.IsCancellationRequested && !ct.IsCancellationRequested)
                        {
                            backgrounded = true;
                        }
                    });

                    tcs.TrySetResult(backgrounded || IsQuietEnd(watched) ? null : DescribeWatch(watched));
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Watching the Workspace CodeGraph index failed. WorkspaceId={WorkspaceId}", workspaceId);
                    tcs.TrySetResult(ex.Message);
                    throw;
                }
            },
            out var operation);

        if (!started)
            return $"A workspace operation is already running ({operation.DisplayMessage}).";

        var result = await tcs.Task.WaitAsync(cancellationToken);
        // The lock is free and the operation has its final state before the caller goes on, so what the caller
        // does next (close the dialog, ask whether a job is still running) does not see this one as running.
        await operation.WhenCompleted.WaitAsync(cancellationToken);
        return result;
    }

    private static bool IsQuietEnd(string? outcome) =>
        outcome is "Completed" or "Stopped" or "NotRunning";

    /// <summary>The build was started; an outcome here is how the build ended, not a failure to start it.</summary>
    private static string DescribeWatch(string? outcome) => outcome switch
    {
        FeatureCodeGraphService.Disabled => Describe(outcome),
        null or "" or FeatureCodeGraphService.Failed => "The CodeGraph index build failed.",
        _ => $"The CodeGraph index build did not finish ({outcome}).",
    };

    private static string Describe(string? outcome) => outcome switch
    {
        "NotInstalled" => "CodeGraph is not installed.",
        "TargetMissing" => "The Workspace folder was not found, so the CodeGraph index was not started.",
        FeatureCodeGraphService.Disabled => "CodeGraph is turned off in the Workspace definition, so the index was not started.",
        null or "" or FeatureCodeGraphService.Failed => "The CodeGraph index could not be started.",
        _ => $"The CodeGraph index was not started ({outcome}).",
    };
}
