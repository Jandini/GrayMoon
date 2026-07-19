using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Queue;
using GrayMoon.Agent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Hosted;

/// <summary>Runs the dedicated diff command queue, sized from <see cref="AgentOptions.MaxConcurrentDiffCommands"/>.</summary>
public sealed class DiffJobBackgroundService(
    IDiffJobQueue jobQueue,
    DiffJobQueue queueTracker,
    ICommandDispatcher dispatcher,
    INotifySyncHandler notifySyncHandler,
    IHubConnectionProvider hubProvider,
    CommandJobCancellationRegistry cancellationRegistry,
    IOptions<AgentOptions> options,
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
