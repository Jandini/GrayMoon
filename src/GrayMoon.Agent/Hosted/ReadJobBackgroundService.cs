using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Queue;
using GrayMoon.Agent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Hosted;

/// <summary>Runs the dedicated read-only command queue, sized from <see cref="AgentOptions.MaxConcurrentReadCommands"/>.</summary>
public sealed class ReadJobBackgroundService(
    IReadJobQueue jobQueue,
    ReadJobQueue queueTracker,
    ICommandDispatcher dispatcher,
    INotifySyncHandler notifySyncHandler,
    IHubConnectionProvider hubProvider,
    CommandJobCancellationRegistry cancellationRegistry,
    IOptions<AgentOptions> options,
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
