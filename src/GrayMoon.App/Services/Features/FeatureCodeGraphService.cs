using System.Text.Json.Serialization;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Application.WorkspaceManifest;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// CodeGraph support for Features. <c>"codegraph"</c> in <c>graymoon.json</c> is the workspace-wide switch: true
/// maintains <c>codegraph.json</c> and the managed <c>.gitignore</c> entry, and Repair Feature starts an index.
/// Create Feature does not read that switch. The New Feature dialog offers the index when the CodeGraph CLI is
/// installed, including when the Workspace has no Workspace repository and no definition; an explicit
/// <c>"codegraph": false</c> hides that offer. The Worker builds each index from scratch in the Feature root, so the
/// Workspace root needs no index of its own. Remove and Roll back always drop a Feature index before any worktree
/// goes, because a ticked box can leave one behind while the switch is off. CodeGraph is optional: every failure is
/// logged and never fails the Feature operation.
/// </summary>
public sealed class FeatureCodeGraphService(
    IWorkspaceManifestService manifestService,
    IWorkerBridge workerBridge,
    IWorkspaceContextPathResolver pathResolver,
    ILogger<FeatureCodeGraphService> logger)
{
    /// <summary>Outcome when the Workspace definition does not turn CodeGraph on; nothing was sent to the Worker.</summary>
    public const string Disabled = "Disabled";

    /// <summary>Outcome when the Worker could not be asked or did not answer.</summary>
    public const string Failed = "Failed";

    /// <summary>
    /// Writes <c>codegraph.json</c> in the Workspace and Feature roots, then asks the Worker to start indexing the
    /// Feature root. Returns the Worker's outcome (<c>Started</c>, <c>AlreadyInitialized</c>, ...),
    /// <see cref="Disabled"/> or <see cref="Failed"/>. When <paramref name="honorDefinitionSwitch"/> is false, an
    /// explicit request (the New Feature checkbox) starts the index even though the definition switch is off or absent.
    /// </summary>
    public async Task<string> InitializeAsync(
        int workspaceId,
        WorkspaceFeatureContextId featureContextId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool honorDefinitionSwitch = true)
    {
        if (honorDefinitionSwitch && !await manifestService.IsCodeGraphEnabledAsync(workspaceId, cancellationToken))
            return Disabled;

        progress?.Report(new OperationProgress("Starting CodeGraph index..."));

        // The Feature indexes its own codegraph.json, so it must list the repositories before init runs.
        await WriteConfigAsync(workspaceId, null, cancellationToken);
        await WriteConfigAsync(workspaceId, featureContextId, cancellationToken);

        var targetRoot = await pathResolver.GetContextRootAsync(featureContextId, cancellationToken);

        var response = await workerBridge.SendCommandAsync(
            WorkerHubMethods.InitCodeGraph,
            new { targetRoot },
            cancellationToken);
        var outcome = ReadOutcome(response, out var message);
        logger.LogInformation(
            "CodeGraph init for Feature root {TargetRoot}: {Outcome}. {Message}",
            targetRoot, outcome, message);
        return outcome;
    }

    /// <summary>
    /// Streams the Feature root's index build (started by <see cref="InitializeAsync"/>) into the active overlay
    /// terminal until it ends. Each Worker call follows the build for a bounded time and says where to resume, so a
    /// long build never hits the App's command timeout. Cancelling stops only the watching; the build goes on. Returns
    /// the build's outcome (<c>Completed</c>, <c>Failed</c>, <c>Stopped</c>, <c>NotRunning</c>), <see cref="Disabled"/>
    /// or <see cref="Failed"/>.
    /// </summary>
    public async Task<string> WatchInitializeAsync(
        int workspaceId,
        WorkspaceFeatureContextId featureContextId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool honorDefinitionSwitch = true)
    {
        if (honorDefinitionSwitch && !await manifestService.IsCodeGraphEnabledAsync(workspaceId, cancellationToken))
            return Disabled;

        var root = await pathResolver.GetContextRootAsync(featureContextId, cancellationToken);
        var from = 0;
        // When no build was started the first answer is an immediate NotRunning, so this shows only for a moment.
        progress?.Report(new OperationProgress("Building CodeGraph index..."));
        while (true)
        {
            var response = await workerBridge.SendCommandAsync(
                WorkerHubMethods.WatchCodeGraphInit,
                new { root, from },
                cancellationToken);
            if (!response.Success)
            {
                logger.LogWarning("Could not watch the CodeGraph index build in {Root}: {Error}", root, response.Error);
                return Failed;
            }

            var data = WorkerResponseJson.DeserializeWorkerResponse<CodeGraphWatchResponse>(response.Data);
            var outcome = string.IsNullOrWhiteSpace(data?.Outcome) ? Failed : data.Outcome;
            if (outcome != "Running")
            {
                if (outcome != "NotRunning")
                    logger.LogInformation("CodeGraph index build for Feature root {Root}: {Outcome}", root, outcome);
                return outcome;
            }

            from = data!.Next;
        }
    }

    /// <summary>
    /// Asks the Worker to remove the Feature root's index (stopping a build still running there). Returns the
    /// Worker's outcome (<c>Removed</c>, <c>NotInitialized</c>, ...) or <see cref="Failed"/>. Runs even when the
    /// definition switch is off: Create can start an index from the dialog without that switch, and a leftover index
    /// blocks removing the worktree. <c>NotInitialized</c> is the no-op when there is nothing to remove.
    /// </summary>
    public async Task<string> UninitializeAsync(
        int workspaceId,
        WorkspaceFeatureContextId featureContextId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new OperationProgress("Removing CodeGraph index..."));

        var root = await pathResolver.GetContextRootAsync(featureContextId, cancellationToken);
        var response = await workerBridge.SendCommandAsync(
            WorkerHubMethods.UninitCodeGraph,
            new { root },
            cancellationToken);
        var outcome = ReadOutcome(response, out var message);
        if (outcome is "Removed" or "NotInitialized")
            logger.LogInformation("CodeGraph uninit for Feature root {Root}: {Outcome}", root, outcome);
        else
            logger.LogWarning("CodeGraph uninit for Feature root {Root}: {Outcome}. {Message}", root, outcome, message);
        return outcome;
    }

    private async Task WriteConfigAsync(int workspaceId, WorkspaceFeatureContextId? contextId, CancellationToken cancellationToken)
    {
        var result = await manifestService.WriteCodeGraphConfigAsync(workspaceId, contextId, cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning(
                "Could not write codegraph.json. WorkspaceId={WorkspaceId} ContextId={ContextId}: {Error}",
                workspaceId, contextId?.Value, result.Error);
        }
    }

    private static string ReadOutcome(WorkerCommandResponse response, out string? message)
    {
        if (!response.Success)
        {
            message = response.Error;
            return Failed;
        }

        var data = WorkerResponseJson.DeserializeWorkerResponse<CodeGraphWorkerResponse>(response.Data);
        message = data?.Message;
        return string.IsNullOrWhiteSpace(data?.Outcome) ? Failed : data.Outcome;
    }

    /// <summary>App-side shape of the Worker's WatchCodeGraphInit response.</summary>
    private sealed class CodeGraphWatchResponse
    {
        [JsonPropertyName("outcome")]
        public string? Outcome { get; set; }

        [JsonPropertyName("next")]
        public int Next { get; set; }
    }

    /// <summary>App-side shape of the Worker's InitCodeGraph / UninitCodeGraph responses.</summary>
    private sealed class CodeGraphWorkerResponse
    {
        [JsonPropertyName("outcome")]
        public string? Outcome { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
