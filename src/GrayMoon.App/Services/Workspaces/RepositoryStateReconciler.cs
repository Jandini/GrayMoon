using System.Collections.Concurrent;
using System.Threading.Channels;
using GrayMoon.App.Services.Git;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// Asked to read a repository again when what is stored for it cannot be trusted: two reports whose order is not
/// known, or evidence that was lost. A request never changes stored state by itself; it schedules one authoritative
/// read of that one repository in that one context.
/// </summary>
public interface IRepositoryStateReconciler
{
    /// <summary>
    /// Queues a read of one repository of one context. A repository that is already queued is not queued again.
    /// Returns false when it was already queued or the queue is full (the periodic audit then covers it). Never throws.
    /// </summary>
    bool Request(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, string reason);
}

/// <summary>
/// Runs the requested reads one at a time, each in a scope of its own, through the existing single-repository
/// refresh (<see cref="WorkspaceGitService.SyncSingleRepositoryAsync"/>): the same read a user's Sync of one
/// repository makes, so its result is ordered against later reports like any operation's. The queue is bounded and
/// keyed, so a burst of ambiguous reports for one repository costs one read. A failed read is logged and not
/// retried here: the stored state is no worse than before, and the periodic audit and the next event still apply.
/// </summary>
public sealed class RepositoryStateReconciler(
    IServiceScopeFactory scopeFactory,
    ILogger<RepositoryStateReconciler> logger) : BackgroundService, IRepositoryStateReconciler
{
    /// <summary>More than this many repositories waiting means something broader is wrong; the audit covers the rest.</summary>
    internal const int Capacity = 256;

    private readonly Channel<ReadRequest> _channel = Channel.CreateBounded<ReadRequest>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    // What is queued and not started yet, so a repository waits at most once.
    private readonly ConcurrentDictionary<(int ContextId, int RepositoryId), byte> _queued = new();

    public bool Request(int workspaceId, WorkspaceFeatureContextId contextId, int repositoryId, string reason)
    {
        if (!_queued.TryAdd((contextId.Value, repositoryId), 0))
            return false;

        if (_channel.Writer.TryWrite(new ReadRequest(workspaceId, contextId, repositoryId, reason)))
        {
            logger.LogInformation(
                "Repository state will be read again: workspace={WorkspaceId}, context={ContextId}, repo={RepositoryId}, reason={Reason}",
                workspaceId, contextId.Value, repositoryId, reason);
            return true;
        }

        _queued.TryRemove((contextId.Value, repositoryId), out _);
        logger.LogWarning(
            "Repository state read not queued (queue full): workspace={WorkspaceId}, context={ContextId}, repo={RepositoryId}, reason={Reason}",
            workspaceId, contextId.Value, repositoryId, reason);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                // From here a new report may queue the repository again: it would be newer than this read.
                _queued.TryRemove((request.ContextId.Value, request.RepositoryId), out _);
                await RunAsync(request, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>One read. Any failure, also a cancellation that is not the shutdown, fails this request only.</summary>
    internal async Task RunAsync(ReadRequest request, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            var (success, error) = await git.SyncSingleRepositoryAsync(
                request.RepositoryId, request.WorkspaceId, request.ContextId, stoppingToken);
            if (!success)
            {
                logger.LogWarning(
                    "Repository state could not be read again: workspace={WorkspaceId}, context={ContextId}, repo={RepositoryId}, reason={Reason}, error={Error}",
                    request.WorkspaceId, request.ContextId.Value, request.RepositoryId, request.Reason, error);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Repository state read failed: workspace={WorkspaceId}, context={ContextId}, repo={RepositoryId}, reason={Reason}",
                request.WorkspaceId, request.ContextId.Value, request.RepositoryId, request.Reason);
        }
    }

    internal sealed record ReadRequest(int WorkspaceId, WorkspaceFeatureContextId ContextId, int RepositoryId, string Reason);
}
