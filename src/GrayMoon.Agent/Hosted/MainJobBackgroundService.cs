using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Hosted;

/// <summary>Runs the main command queue, sized from <see cref="AgentOptions.MaxConcurrentCommands"/>.</summary>
public sealed class MainJobBackgroundService(
    IJobQueue jobQueue,
    IAgentQueueTracker queueTracker,
    ICommandDispatcher dispatcher,
    INotifySyncHandler notifySyncHandler,
    IHubConnectionProvider hubProvider,
    CommandJobCancellationRegistry cancellationRegistry,
    IOptions<AgentOptions> options,
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
