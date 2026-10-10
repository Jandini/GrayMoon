using GrayMoon.App.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Pushes Windows desktop notifications for worker lifecycle events that the in-app badge
/// alone can miss (window unfocused, user on another page). Version mismatch, an in-progress
/// self-update disconnect, and a genuine offline are distinct events - the policy decides
/// which one to send so an upgrade never looks like a surprise failure.
/// The first mismatch of this process, before any matching Worker has connected, is a dialog
/// (<see cref="WorkerUpgradePrompt"/>) instead of that balloon. Clicking a notification
/// (handled on the GrayMoon.Desktop side) opens the Worker page.
/// Best-effort - <see cref="IHubContext{DesktopNotificationHub}"/> silently sends to no one
/// when GrayMoon.Desktop is not connected (e.g. running in the browser, not desktop mode).
/// </summary>
public sealed class WorkerUpdateDesktopNotifier(
    WorkerConnectionTracker workerConnectionTracker,
    WorkerUpgradeStartupPrompt workerUpgradeStartupPrompt,
    IHubContext<DesktopNotificationHub> desktopHub,
    ILogger<WorkerUpdateDesktopNotifier> logger) : IHostedService
{
    private readonly WorkerDesktopNotificationPolicy _policy = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        workerConnectionTracker.OnStateChanged(OnWorkerStateChanged);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void OnWorkerStateChanged(WorkerConnectionState state)
    {
        var observation = workerUpgradeStartupPrompt.Observe(
            state,
            workerConnectionTracker.IsSelfUpdateInProgress,
            workerConnectionTracker.WorkerSemVer,
            workerConnectionTracker.AppSemVer);
        if (observation.Prompt is not null)
            _ = SendPromptAsync(observation.Prompt);

        var notification = _policy.OnChange(
            state,
            workerConnectionTracker.IsSelfUpdateInProgress,
            workerConnectionTracker.WorkerSemVer,
            workerConnectionTracker.LogonPasswordRequired);
        if (observation.SuppressUpdateNotification || notification is null)
            return;

        _ = SendAsync(notification);
    }

    private async Task SendPromptAsync(WorkerUpgradePrompt prompt)
    {
        try
        {
            await desktopHub.Clients.All.SendAsync(WorkerUpgradePrompt.ClientEventName, prompt);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to prompt for a Worker upgrade");
        }
    }

    private async Task SendAsync(DesktopNotification notification)
    {
        try
        {
            await desktopHub.Clients.All.SendAsync("Notify", notification);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to push worker desktop notification ({Title})", notification.Title);
        }
    }
}
