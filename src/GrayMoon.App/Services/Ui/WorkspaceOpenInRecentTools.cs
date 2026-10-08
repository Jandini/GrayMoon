using GrayMoon.App.Components.Features;
using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.WorkspaceManifest;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Ui;

/// <summary>
/// Recent Open-in tools for one Workspace. Persisted in <c>.graymoon.json</c> only when that
/// Workspace has a Workspace repository and the file is already there. Otherwise the list lasts
/// for this circuit.
/// </summary>
public sealed class WorkspaceOpenInRecentTools(
    IWorkerBridge workerBridge,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceFeatureContextResolver contextResolver,
    ILogger<WorkspaceOpenInRecentTools> logger)
{
    private readonly Dictionary<int, IReadOnlyList<string>> _session = new();

    public async Task<IReadOnlyList<string>> GetAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var stored = await TryReadStoredAsync(workspaceId, cancellationToken);
        if (stored is not null)
        {
            _session[workspaceId] = stored;
            return stored;
        }

        return _session.TryGetValue(workspaceId, out var session) ? session : [];
    }

    public async Task<IReadOnlyList<string>> RecordAsync(
        int workspaceId,
        string toolId,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(workspaceId, cancellationToken);
        var next = FeatureOpenInTools.RecordUse(current, toolId);
        _session[workspaceId] = next;
        if (!Same(current, next))
            await TryWriteAsync(workspaceId, next, cancellationToken);
        return next;
    }

    private async Task<IReadOnlyList<string>?> TryReadStoredAsync(int workspaceId, CancellationToken cancellationToken)
    {
        try
        {
            var args = await GetArgsAsync(workspaceId, cancellationToken);
            if (args?.WorkspaceRepositoryName is null)
                return null;

            var read = await WorkspaceRepositoryFileAccess.ReadAsync(
                workerBridge, args, WorkspaceRepositoryFileAccess.ManifestFilePath, cancellationToken);
            if (read.Error is not null || !read.Found)
                return null;

            return WorkspaceManifestRecentTools.Read(read.Content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read recent Open-in tools. WorkspaceId={WorkspaceId}", workspaceId);
            return null;
        }
    }

    private async Task TryWriteAsync(int workspaceId, IReadOnlyList<string> tools, CancellationToken cancellationToken)
    {
        if (tools.Count == 0)
            return;

        try
        {
            var args = await GetArgsAsync(workspaceId, cancellationToken);
            if (args?.WorkspaceRepositoryName is null)
                return;

            var read = await WorkspaceRepositoryFileAccess.ReadAsync(
                workerBridge, args, WorkspaceRepositoryFileAccess.ManifestFilePath, cancellationToken);
            if (read.Error is not null || !read.Found || read.Content is null)
                return;

            var updated = WorkspaceManifestRecentTools.Apply(read.Content, tools);
            if (updated is null)
                return;

            var result = await WorkspaceRepositoryFileAccess.WriteAsync(
                workerBridge, args, WorkspaceRepositoryFileAccess.ManifestFilePath, updated, cancellationToken);
            if (!result.Success)
                logger.LogWarning("Could not save recent Open-in tools. WorkspaceId={WorkspaceId} Error={Error}", workspaceId, result.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not save recent Open-in tools. WorkspaceId={WorkspaceId}", workspaceId);
        }
    }

    private async Task<WorkerWorkspaceArgs?> GetArgsAsync(int workspaceId, CancellationToken cancellationToken)
    {
        if (workspaceId <= 0)
            return null;

        var contextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        return await pathResolver.GetWorkerArgsAsync(contextId, cancellationToken);
    }

    private static bool Same(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
