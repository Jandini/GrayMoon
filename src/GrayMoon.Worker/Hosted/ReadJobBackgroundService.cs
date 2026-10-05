using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Queue;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Hosted;

/// <summary>Runs the dedicated read-only command queue, sized from <see cref="WorkerOptions.MaxConcurrentReadCommands"/>.</summary>
public sealed class ReadJobBackgroundService(
    IReadJobQueue jobQueue,
    ReadJobQueue queueTracker,
    ICommandDispatcher dispatcher,
    INotifySyncHandler notifySyncHandler,
    IHubConnectionProvider hubProvider,
    CommandJobCancellationRegistry cancellationRegistry,
    IOptions<WorkerOptions> options,
    ILogger<ReadJobBackgroundService> logger)
    : JobBackgroundService(
        jobQueue,
        queueTracker,
        dispatcher,
        notifySyncHandler,
        hubProvider,
        cancellationRegistry,
        options.Value.MaxConcurrentReadCommands,
        logger);
