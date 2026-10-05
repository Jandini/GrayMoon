using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Hosted;

/// <summary>Runs the main command queue, sized from <see cref="WorkerOptions.MaxConcurrentCommands"/>.</summary>
public sealed class MainJobBackgroundService(
    IJobQueue jobQueue,
    IWorkerQueueTracker queueTracker,
    ICommandDispatcher dispatcher,
    INotifySyncHandler notifySyncHandler,
    IHubConnectionProvider hubProvider,
    CommandJobCancellationRegistry cancellationRegistry,
    IOptions<WorkerOptions> options,
    ILogger<MainJobBackgroundService> logger)
    : JobBackgroundService(
        jobQueue,
        queueTracker,
        dispatcher,
        notifySyncHandler,
        hubProvider,
        cancellationRegistry,
        options.Value.MaxConcurrentCommands,
        logger);
