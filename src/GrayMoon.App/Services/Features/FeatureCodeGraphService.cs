using System.Text.Json.Serialization;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Application.WorkspaceManifest;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// CodeGraph support for Features, active only when the Workspace definition has <c>"codegraph": true</c>. Create
/// Feature gives the new Feature root its own index when the Workspace root has one (the Worker builds it in the
/// background); Remove Feature removes that index before any worktree is removed. CodeGraph is optional: every
/// failure is logged and never fails the Feature operation.
/// </summary>
public sealed class FeatureCodeGraphService(
    IWorkspaceManifestService manifestService,
    IWorkerBridge workerBridge,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceFeatureContextResolver contextResolver,
    ILogger<FeatureCodeGraphService> logger)
{
    /// <summary>Outcome when the Workspace definition does not turn CodeGraph on; nothing was sent to the Worker.</summary>
    public const string Disabled = "Disabled";

    /// <summary>Outcome when the Worker could not be asked or did not answer.</summary>
    public const string Failed = "Failed";

    /// <summary>
    /// Writes <c>codegraph.json</c> in the Workspace and Feature roots, then asks the Worker to start indexing the
    /// Feature root. Returns the Worker's outcome (<c>Started</c>, <c>SourceNotInitialized</c>, ...),
    /// <see cref="Disabled"/> or <see cref="Failed"/>.
    /// </summary>
    public async Task<string> InitializeAsync(
        int workspaceId,
        WorkspaceFeatureContextId featureContextId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await manifestService.IsCodeGraphEnabledAsync(workspaceId, cancellationToken))
            return Disabled;

        progress?.Report(new OperationProgress("Starting CodeGraph index..."));

        // The Feature indexes its own codegraph.json, so it must list the repositories before init runs.
        await WriteConfigAsync(workspaceId, null, cancellationToken);
        await WriteConfigAsync(workspaceId, featureContextId, cancellationToken);

        var specialContextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        var sourceRoot = await pathResolver.GetContextRootAsync(specialContextId, cancellationToken);
        var targetRoot = await pathResolver.GetContextRootAsync(featureContextId, cancellationToken);

        var response = await workerBridge.SendCommandAsync(
            WorkerHubMethods.InitCodeGraph,
            new { sourceRoot, targetRoot },
            cancellationToken);
        var outcome = ReadOutcome(response, out var message);
        logger.LogInformation(
            "CodeGraph init for Feature root {TargetRoot}: {Outcome}. {Message}",
            targetRoot, outcome, message);
        return outcome;
    }

    /// <summary>
    /// Asks the Worker to remove the Feature root's index (stopping a build still running there). Returns the
    /// Worker's outcome (<c>Removed</c>, <c>NotInitialized</c>, ...), <see cref="Disabled"/> or <see cref="Failed"/>.
    /// </summary>
    public async Task<string> UninitializeAsync(
        int workspaceId,
        WorkspaceFeatureContextId featureContextId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await manifestService.IsCodeGraphEnabledAsync(workspaceId, cancellationToken))
            return Disabled;

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

    /// <summary>App-side shape of the Worker's InitCodeGraph / UninitCodeGraph responses.</summary>
    private sealed class CodeGraphWorkerResponse
    {
        [JsonPropertyName("outcome")]
        public string? Outcome { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
