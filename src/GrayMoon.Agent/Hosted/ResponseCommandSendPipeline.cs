using System.Net.Http;
using System.Net.WebSockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace GrayMoon.Agent.Hosted;

/// <summary>
/// Retries <see cref="HubConnection.InvokeAsync"/> only for likely-transient transport failures.
/// Does not retry when the failure is consistent with SignalR maximum message size / payload rejection.
/// </summary>
internal static class ResponseCommandSendPipeline
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(400);

    public static ResiliencePipeline Create(ILogger logger) =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ShouldRetry),
                MaxRetryAttempts = 3,
                Delay = InitialDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger.LogWarning(
                        args.Outcome.Exception,
                        "ResponseCommand send retry attempt {Attempt} (up to {MaxRetryAttempts} retries after the initial call)",
                        args.AttemptNumber,
                        3);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();

    /// <summary>True when the exception should be retried by Polly.</summary>
    public static bool ShouldRetry(Exception ex)
    {
        if (ex is OperationCanceledException)
            return false;

        if (IsLikelySignalRMessageSizeOrPayloadRejection(ex))
            return false;

        if (ex is IOException or TimeoutException or HttpRequestException or WebSocketException)
            return true;

        if (ex is HubException hubEx && IsLikelyTransientHubException(hubEx))
            return true;

        // Transient disconnect / negotiation issues sometimes surface as HttpIOException (e.g. HTTP/2 reset).
        if (ex is HttpIOException)
            return true;

        return false;
    }

    private static bool IsLikelySignalRMessageSizeOrPayloadRejection(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            var m = e.Message;
            if (string.IsNullOrEmpty(m))
                continue;

            if (m.Contains("Maximum message size", StringComparison.OrdinalIgnoreCase))
                return true;
            if (m.Contains("MaximumReceiveMessageSize", StringComparison.OrdinalIgnoreCase))
                return true;
            if (m.Contains("32768", StringComparison.Ordinal))
                return true;
            if (m.Contains("maximum message size of", StringComparison.OrdinalIgnoreCase) && m.Contains("exceeded", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsLikelyTransientHubException(HubException ex)
    {
        var m = ex.Message;

        if (string.IsNullOrEmpty(m))
            return false;

        // Ambiguous server-side close; allow one retry for flaky networks (avoid if we matched payload rejection above).
        if (m.Contains("Connection closed", StringComparison.OrdinalIgnoreCase))
            return true;
        if (m.Contains("connection was closed", StringComparison.OrdinalIgnoreCase))
            return true;
        if (m.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return true;
        if (m.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
