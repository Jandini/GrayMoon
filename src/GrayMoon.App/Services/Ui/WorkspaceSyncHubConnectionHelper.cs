using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace GrayMoon.App.Services.Ui;

/// <summary>
/// Builds and tears down the browser-side connection to the workspace-sync SignalR hub
/// (<c>/hubs/workspace-sync</c>). Every page/component that reacts to workspace-sync broadcasts
/// (<c>WorkspaceSynced</c>, <c>RepositorySynced</c>, <c>GitChangesUpdated</c>, <c>RepositoryError</c>, etc.)
/// uses the same hub URL and reconnect policy - only the specific event subscriptions and handler logic
/// differ per caller, and those stay at each call site rather than being unified here.
/// </summary>
public static class WorkspaceSyncHubConnectionHelper
{
    private const string HubPath = "/hubs/workspace-sync";

    /// <summary>
    /// Builds (but does not start) a connection to the workspace-sync hub with automatic reconnect.
    /// Callers register their own <c>.On(...)</c> handlers and then call <see cref="HubConnection.StartAsync"/>.
    /// </summary>
    public static HubConnection Create(NavigationManager navigationManager) =>
        new HubConnectionBuilder()
            .WithUrl(ResolveHubUri(navigationManager))
            .WithAutomaticReconnect()
            .Build();

    /// <summary>
    /// The hub is on the App's own host. A Desktop-launched App listens on 127.0.0.1 only, and localhost tries ::1
    /// first, which stalls the connect for seconds before falling back, so a loopback name is rewritten to the IPv4 address.
    /// </summary>
    internal static Uri ResolveHubUri(NavigationManager navigationManager)
    {
        var uri = navigationManager.ToAbsoluteUri(HubPath);
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(uri) { Host = "127.0.0.1" }.Uri
            : uri;
    }

    /// <summary>
    /// Starts <paramref name="hubConnection"/> and never throws: a failed first start (circuit closing, App busy) must not
    /// end the Blazor circuit, and live updates are an enhancement the page can do without. Returns whether it connected.
    /// </summary>
    /// <remarks>
    /// <c>WithAutomaticReconnect</c> only covers a connection that was up once, so a failed first start is retried
    /// here after each of <see cref="StartRetryDelays"/>. The retries end when the page disposed the connection or
    /// cancelled <paramref name="cancellationToken"/>. One debug line records the outcome.
    /// </remarks>
    public static Task<bool> TryStartAsync(HubConnection hubConnection, ILogger logger, CancellationToken cancellationToken = default) =>
        TryStartAsync(hubConnection.StartAsync, logger, TimeProvider.System, cancellationToken);

    /// <summary>Waits before the second, third and fourth start attempt. After the last one the page stays without live updates until it is reloaded.</summary>
    internal static readonly TimeSpan[] StartRetryDelays = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    internal static async Task<bool> TryStartAsync(
        Func<CancellationToken, Task> start,
        ILogger logger,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await start(cancellationToken);
                LogOutcome(logger, "started", attempt, timeProvider.GetElapsedTime(started), null);
                return true;
            }
            catch (Exception ex) when (ex is ObjectDisposedException || cancellationToken.IsCancellationRequested)
            {
                // The page went away: its Dispose disposed the connection or cancelled the token. Not a failure.
                LogOutcome(logger, "cancelled by dispose", attempt, timeProvider.GetElapsedTime(started), null);
                return false;
            }
            catch (Exception ex)
            {
                if (attempt > StartRetryDelays.Length)
                {
                    LogOutcome(logger, "failed", attempt, timeProvider.GetElapsedTime(started), ex);
                    return false;
                }

                logger.LogDebug(ex, "Workspace-sync hub start attempt {Attempt} failed; retrying in {Delay}", attempt, StartRetryDelays[attempt - 1]);
            }

            try
            {
                await Task.Delay(StartRetryDelays[attempt - 1], timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                LogOutcome(logger, "cancelled by dispose", attempt, timeProvider.GetElapsedTime(started), null);
                return false;
            }
        }
    }

    private static void LogOutcome(ILogger logger, string outcome, int attempts, TimeSpan elapsed, Exception? exception) =>
        logger.LogDebug(
            exception,
            "Workspace-sync hub start for {Page}: {Outcome} after {Attempts} attempt(s) in {ElapsedMs} ms",
            logger.GetType().IsGenericType ? logger.GetType().GetGenericArguments()[0].Name : "page",
            outcome, attempts, (long)elapsed.TotalMilliseconds);


    /// <summary>
    /// Stops and disposes <paramref name="hubConnection"/> without awaiting completion - the fire-and-forget
    /// teardown used by synchronous <c>Dispose()</c> methods that cannot await. Not used by components whose
    /// teardown is itself async (e.g. an <see cref="IAsyncDisposable"/> that awaits disposal directly).
    /// </summary>
    public static void DisposeFireAndForget(HubConnection? hubConnection)
    {
        _ = hubConnection?.StopAsync();
        _ = hubConnection?.DisposeAsync().AsTask();
    }
}
