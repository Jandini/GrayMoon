using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Queue;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Hosted;

/// <summary>Runs the dedicated diff command queue, sized from <see cref="WorkerOptions.MaxConcurrentDiffCommands"/>.</summary>
public sealed class DiffJobBackgroundService(
    IDiffJobQueue jobQueue,
    DiffJobQueue queueTracker,
    ICommandDispatcher dispatcher,
    INotifySyncHandler notifySyncHandler,
    IHubConnectionProvider hubProvider,
    CommandJobCancellationRegistry cancellationRegistry,
    IOptions<WorkerOptions> options,
    ILogger<DiffJobBackgroundService> logger)
    : JobBackgroundService(
        jobQueue,
        queueTracker,
        dispatcher,
        notifySyncHandler,
        hubProvider,
        cancellationRegistry,
        options.Value.MaxConcurrentDiffCommands,
        logger);
