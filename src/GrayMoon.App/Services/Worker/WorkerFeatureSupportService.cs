using GrayMoon.App.Models.Api;

namespace GrayMoon.App.Services.Worker;

/// <summary>Tells whether the connected Worker advertises a feature in its <c>GetCapabilities</c> response (D2).</summary>
public interface IWorkerFeatureSupportService
{
    /// <summary>
    /// True when the connected Worker lists <paramref name="feature"/> in <c>supportedFeatures</c>. False when the
    /// Worker is disconnected, the call fails, or the list is missing (an older Worker).
    /// </summary>
    Task<bool> SupportsAsync(string feature, CancellationToken cancellationToken = default);
}

/// <summary>
/// Singleton. Asks the Worker for its capabilities (a cheap command, no child processes) through a short-lived scope (the bridge is scoped) and caches the
/// advertised feature list for 60 seconds. A disconnected Worker or a failed call is never cached.
/// </summary>
public sealed class WorkerFeatureSupportService(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkerFeatureSupportService> logger,
    TimeProvider? timeProvider = null) : IWorkerFeatureSupportService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private IReadOnlyList<string>? _cachedFeatures;
    private DateTimeOffset _cachedUntil;

    public async Task<bool> SupportsAsync(string feature, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feature))
            return false;

        var features = TryGetCached() ?? await LoadAsync(cancellationToken);
        return features is not null
            && features.Contains(feature, StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyList<string>? TryGetCached()
    {
        lock (_gate)
        {
            return _cachedFeatures is not null && _timeProvider.GetUtcNow() < _cachedUntil
                ? _cachedFeatures
                : null;
        }
    }

    private async Task<IReadOnlyList<string>?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var workerBridge = scope.ServiceProvider.GetRequiredService<IWorkerBridge>();
            if (!workerBridge.IsWorkerConnected)
                return null;

            var response = await workerBridge.SendCommandAsync("GetCapabilities", new { }, cancellationToken);
            if (!response.Success || response.Data is null)
                return null;

            var capabilities = WorkerResponseJson.DeserializeWorkerResponse<GetCapabilitiesWorkerResponse>(response.Data);
            if (capabilities?.SupportedFeatures is null)
                return null;

            var features = capabilities.SupportedFeatures.ToList();
            lock (_gate)
            {
                _cachedFeatures = features;
                _cachedUntil = _timeProvider.GetUtcNow() + CacheDuration;
            }

            return features;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the Worker's supported features");
            return null;
        }
    }
}
