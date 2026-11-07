using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Queue;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Hosted;

/// <summary>Runs the dedicated AI command queue, sized from <see cref="WorkerOptions.MaxConcurrentAiCommands"/>.</summary>
public sealed class AiJobBackgroundService(
    IAiJobQueue jobQueue,
    AiJobQueue queueTracker,
    ICommandDispatcher dispatcher,
    INotifySyncHandler notifySyncHandler,
    IHubConnectionProvider hubProvider,
    CommandJobCancellationRegistry cancellationRegistry,
    IOptions<WorkerOptions> options,
    ILogger<AiJobBackgroundService> logger)
    : JobBackgroundService(
        jobQueue,
        queueTracker,
        dispatcher,
        notifySyncHandler,
        hubProvider,
        cancellationRegistry,
        options.Value.MaxConcurrentAiCommands,
        logger);
