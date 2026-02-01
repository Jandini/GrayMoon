using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GrayMoon.App.Services;

/// <summary>
/// Background service that processes sync requests from a channel with controlled parallelism.
/// </summary>
public class SyncBackgroundService : BackgroundService
{
    private readonly Channel<SyncRequest> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SyncBackgroundService> _logger;
    private readonly int _maxConcurrency;
    private readonly bool _enableDeduplication;
    private readonly ConcurrentDictionary<SyncRequest, byte> _inFlightRequests;

    public SyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<SyncBackgroundService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Default to 8 parallel sync tasks, configurable via appsettings
        _maxConcurrency = configuration.GetValue<int?>("Sync:MaxConcurrency") ?? 8;
        _enableDeduplication = configuration.GetValue<bool?>("Sync:EnableDeduplication") ?? true;

        // Unbounded channel - all sync requests are queued (could make this bounded with a limit)
        _channel = Channel.CreateUnbounded<SyncRequest>(new UnboundedChannelOptions
        {
            SingleReader = false, // multiple workers read from the channel
            SingleWriter = false  // multiple API calls write to the channel
        });

        _inFlightRequests = new ConcurrentDictionary<SyncRequest, byte>();

        _logger.LogInformation("SyncBackgroundService initialized with max concurrency: {MaxConcurrency}, deduplication: {Deduplication}",
            _maxConcurrency, _enableDeduplication);
    }

    /// <summary>
    /// Enqueues a sync request. Returns true if enqueued, false if channel is closed or already in flight (when deduplication enabled).
    /// </summary>
    public bool EnqueueSync(int repositoryId, int workspaceId)
    {
        var request = new SyncRequest(repositoryId, workspaceId);

        // Deduplication: skip if same request is already queued or being processed
        if (_enableDeduplication && !_inFlightRequests.TryAdd(request, 0))
        {
            _logger.LogDebug("Skipped duplicate sync request: repositoryId={RepositoryId}, workspaceId={WorkspaceId}", repositoryId, workspaceId);
            return true; // Return true since the sync will happen (just not a new one)
        }

        if (_channel.Writer.TryWrite(request))
        {
            _logger.LogDebug("Enqueued sync request: repositoryId={RepositoryId}, workspaceId={WorkspaceId}", repositoryId, workspaceId);
            return true;
        }

        // Failed to enqueue - remove from in-flight tracking
        if (_enableDeduplication)
            _inFlightRequests.TryRemove(request, out _);

        _logger.LogWarning("Failed to enqueue sync request (channel closed): repositoryId={RepositoryId}, workspaceId={WorkspaceId}", repositoryId, workspaceId);
        return false;
    }

    /// <summary>
    /// Returns the approximate number of pending sync requests in the queue.
    /// </summary>
    public int GetQueueDepth() => _channel.Reader.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SyncBackgroundService starting with {MaxConcurrency} workers", _maxConcurrency);

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
            _logger.LogInformation("SyncBackgroundService stopping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncBackgroundService encountered an error");
        }
    }

    private async Task ProcessQueueAsync(int workerId, CancellationToken stoppingToken)
    {
        _logger.LogDebug("Worker {WorkerId} started", workerId);

        await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                _logger.LogDebug("Worker {WorkerId} processing sync: repositoryId={RepositoryId}, workspaceId={WorkspaceId}",
                    workerId, request.RepositoryId, request.WorkspaceId);

                await using var scope = _scopeFactory.CreateAsyncScope();
                var svc = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
                await svc.SyncSingleRepositoryAsync(request.RepositoryId, request.WorkspaceId, stoppingToken);

                _logger.LogDebug("Worker {WorkerId} completed sync: repositoryId={RepositoryId}, workspaceId={WorkspaceId}",
                    workerId, request.RepositoryId, request.WorkspaceId);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Worker {WorkerId} cancelled", workerId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker {WorkerId} failed to sync repository {RepositoryId} in workspace {WorkspaceId}",
                    workerId, request.RepositoryId, request.WorkspaceId);
                // Continue processing other requests
            }
            finally
            {
                // Remove from in-flight tracking after processing (success or failure)
                if (_enableDeduplication)
                    _inFlightRequests.TryRemove(request, out _);
            }
        }

        _logger.LogDebug("Worker {WorkerId} stopped", workerId);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SyncBackgroundService stopping - completing channel");
        _channel.Writer.Complete();
        await base.StopAsync(cancellationToken);
    }

    private record SyncRequest(int RepositoryId, int WorkspaceId);
}
