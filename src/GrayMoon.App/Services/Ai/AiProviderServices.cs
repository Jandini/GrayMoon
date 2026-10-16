using System.Text.Json.Serialization;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Services.Worker;
using GrayMoon.Common.Ai;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Ai;

/// <summary><c>AiGeneration</c> configuration section.</summary>
public sealed class AiGenerationOptions
{
    public const string SectionName = "AiGeneration";

    /// <summary>Seconds one provider call may take before its process is killed. Kept below the Worker command timeout.</summary>
    public int TimeoutSeconds { get; init; } = 120;
}

/// <summary>One provider as the Worker's account sees it.</summary>
public sealed record AiProviderAvailability(string ProviderId, bool IsInstalled, string? Version, bool HasNoTools, string? Problem);

/// <summary>
/// Which AI CLIs the Worker can run, cached briefly for the whole App so opening Changes or the pull request dialog does
/// not probe the CLIs every time. An explicit refresh, or a Worker that was not reachable, asks again.
/// </summary>
public sealed class AiProviderAvailabilityCache(ILogger<AiProviderAvailabilityCache> logger)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (IReadOnlyList<AiProviderAvailability> Providers, DateTimeOffset At)? _cached;

    /// <param name="workerBridge">The caller's bridge: the bridge is scoped (per circuit), the cache is shared by the whole App.</param>
    public async Task<IReadOnlyList<AiProviderAvailability>> GetAsync(IWorkerBridge workerBridge, bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && _cached is { } fresh && DateTimeOffset.UtcNow - fresh.At < Lifetime)
            return fresh.Providers;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _cached is { } again && DateTimeOffset.UtcNow - again.At < Lifetime)
                return again.Providers;

            var unavailable = workerBridge.GetUnavailableReason();
            if (unavailable is not null)
                return Unavailable(unavailable);

            var response = await workerBridge.SendCommandAsync(WorkerHubMethods.GetAiProviders, new { refresh }, cancellationToken);
            var data = response.Success ? WorkerResponseJson.DeserializeWorkerResponse<GetAiProvidersWorkerResponse>(response.Data) : null;
            if (data is null)
            {
                logger.LogWarning("Checking AI CLIs on the Worker failed: {Error}", response.Error);
                return Unavailable(response.Error ?? "The Worker could not check the AI CLIs.");
            }

            var providers = AiProviderIds.All
                .Select(id => data.Providers.FirstOrDefault(p => p.ProviderId == id) is { } p
                    ? new AiProviderAvailability(id, p.IsInstalled, p.Version, p.HasNoTools, p.Problem)
                    : new AiProviderAvailability(id, false, null, true, "The Worker does not support this provider."))
                .ToList();
            _cached = (providers, DateTimeOffset.UtcNow);
            return providers;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<AiProviderAvailability> Unavailable(string reason) =>
        AiProviderIds.All.Select(id => new AiProviderAvailability(id, false, null, true, reason)).ToList();

    internal sealed class GetAiProvidersWorkerResponse
    {
        [JsonPropertyName("providers")]
        public List<Entry> Providers { get; set; } = [];

        internal sealed class Entry
        {
            [JsonPropertyName("providerId")]
            public string? ProviderId { get; set; }

            [JsonPropertyName("isInstalled")]
            public bool IsInstalled { get; set; }

            [JsonPropertyName("version")]
            public string? Version { get; set; }

            [JsonPropertyName("hasNoTools")]
            public bool HasNoTools { get; set; }

            [JsonPropertyName("problem")]
            public string? Problem { get; set; }
        }
    }
}

/// <summary>
/// The one AI provider pinned per Workspace on this machine (local database, never <c>graymoon.json</c>). Shared by the
/// Changes page and the pull request dialog. Factory-created contexts, so it is safe beside the circuit's own work.
/// </summary>
public sealed class WorkspaceAiProviderPins(IDbContextFactory<AppDbContext> dbContextFactory)
{
    public async Task<string?> GetAsync(int workspaceId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var pinned = await db.Workspaces.AsNoTracking()
            .Where(w => w.WorkspaceId == workspaceId)
            .Select(w => w.PinnedAiProviderId)
            .FirstOrDefaultAsync(cancellationToken);
        return AiProviderIds.IsKnown(pinned) ? pinned : null;
    }

    public async Task SetAsync(int workspaceId, string? providerId, CancellationToken cancellationToken)
    {
        if (providerId is not null && !AiProviderIds.IsKnown(providerId))
            throw new ArgumentException($"Unknown AI provider \"{providerId}\".", nameof(providerId));

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await db.Workspaces
            .Where(w => w.WorkspaceId == workspaceId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.PinnedAiProviderId, providerId), cancellationToken);
    }
}
