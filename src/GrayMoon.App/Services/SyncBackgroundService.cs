using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GrayMoon.App.Services;

/// <summary>
/// Background service that processes sync requests from a channel with controlled parallelism.
/// </summary>
public class SyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<SyncBackgroundService> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly int _maxConcurrency = configuration.GetValue<int?>("Sync:MaxConcurrency") ?? 8;
    private readonly bool _enableDeduplication = configuration.GetValue<bool?>("Sync:EnableDeduplication") ?? true;
    private readonly Channel<SyncRequestItem> _channel = Channel.CreateUnbounded<SyncRequestItem>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<(int RepositoryId, int WorkspaceId), byte> _inFlightRequests = new();

    /// <summary>
    /// Enqueues a sync request. Returns true if enqueued, false if channel is closed or already in flight (when deduplication enabled).
    /// </summary>
    /// <param name="trigger">What triggered the sync (e.g. "post-commit", "post-checkout", "manual") for logging.</param>
    public bool EnqueueSync(int repositoryId, int workspaceId, string? trigger = null)
    {
        var request = new SyncRequestItem(repositoryId, workspaceId, trigger ?? "api");
        var dedupKey = (repositoryId, workspaceId);

        // Deduplication: skip if same repo+workspace is already queued or being processed
        if (_enableDeduplication && !_inFlightRequests.TryAdd(dedupKey, 0))
        {
            logger.LogDebug("Skipped duplicate sync request. Trigger={Trigger}, repositoryId={RepositoryId}, workspaceId={WorkspaceId}", request.Trigger, repositoryId, workspaceId);
            return true; // Return true since the sync will happen (just not a new one)
        }

        if (_channel.Writer.TryWrite(request))
        {
            logger.LogDebug("Enqueued sync request. Trigger={Trigger}, repositoryId={RepositoryId}, workspaceId={WorkspaceId}", request.Trigger, repositoryId, workspaceId);
            return true;
        }

        // Failed to enqueue - remove from in-flight tracking
        if (_enableDeduplication)
            _inFlightRequests.TryRemove(dedupKey, out _);

        logger.LogWarning("Failed to enqueue sync request (channel closed). Trigger={Trigger}, repositoryId={RepositoryId}, workspaceId={WorkspaceId}", request.Trigger, repositoryId, workspaceId);
        return false;
    }

    /// <summary>
    /// Returns the approximate number of pending sync requests in the queue.
    /// </summary>
    public int GetQueueDepth() => _channel.Reader.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SyncBackgroundService starting with {MaxConcurrency} workers", _maxConcurrency);

        // Create N worker tasks that process items from the channel
        var workers = Enumerable.Range(0, _maxConcurrency)
            .Select(workerId => ProcessQueueAsync(workerId, stoppingToken))
            .ToArray();

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("SyncBackgroundService stopping");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SyncBackgroundService encountered an error");
        }
    }

    private async Task ProcessQueueAsync(int workerId, CancellationToken stoppingToken)
    {
        logger.LogDebug("Worker {WorkerId} started", workerId);

        await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                logger.LogInformation("Processing sync. Trigger={Trigger}, repositoryId={RepositoryId}, workspaceId={WorkspaceId}, workerId={WorkerId}",
                    request.Trigger, request.RepositoryId, request.WorkspaceId, workerId);

                await using var scope = scopeFactory.CreateAsyncScope();
                var svc = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                await svc.SyncSingleRepositoryAsync(request.RepositoryId, request.WorkspaceId, stoppingToken);

                logger.LogDebug("Worker {WorkerId} completed sync. Trigger={Trigger}, repositoryId={RepositoryId}, workspaceId={WorkspaceId}",
                    workerId, request.Trigger, request.RepositoryId, request.WorkspaceId);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Worker {WorkerId} cancelled", workerId);
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker {WorkerId} failed to sync repository {RepositoryId} in workspace {WorkspaceId} (trigger={Trigger})",
                    workerId, request.RepositoryId, request.WorkspaceId, request.Trigger);
                // Continue processing other requests
            }
            finally
            {
                // Remove from in-flight tracking after processing (success or failure)
                if (_enableDeduplication)
                    _inFlightRequests.TryRemove((request.RepositoryId, request.WorkspaceId), out _);
            }
        }

        logger.LogDebug("Worker {WorkerId} stopped", workerId);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("SyncBackgroundService stopping - completing channel");
        _channel.Writer.Complete();
        await base.StopAsync(cancellationToken);
    }

    private record SyncRequestItem(int RepositoryId, int WorkspaceId, string Trigger);
}
