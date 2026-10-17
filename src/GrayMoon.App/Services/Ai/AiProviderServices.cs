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
/// Which AI CLIs the Worker can run. Taken from the startup host check (<see cref="HostInfoCache"/>), so opening
/// Changes or the pull request dialog does not probe the CLIs.
/// </summary>
public sealed class AiProviderAvailabilityCache(HostInfoCache hostInfo)
{
    /// <param name="workerBridge">The caller's bridge: the bridge is scoped (per circuit), the host check is shared by the whole App.</param>
    public async Task<IReadOnlyList<AiProviderAvailability>> GetAsync(IWorkerBridge workerBridge, CancellationToken cancellationToken)
    {
        var query = await hostInfo.GetAsync(workerBridge, refresh: false, cancellationToken);
        return query.Read?.AiProviders ?? Unavailable(query.Error ?? "The Worker could not check the AI CLIs.");
    }

    private static IReadOnlyList<AiProviderAvailability> Unavailable(string reason) =>
        AiProviderIds.All.Select(id => new AiProviderAvailability(id, false, null, id != AiProviderIds.Cursor, reason)).ToList();
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
