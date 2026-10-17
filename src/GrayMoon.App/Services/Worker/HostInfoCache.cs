using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Services.Ai;
using GrayMoon.Common.Ai;

namespace GrayMoon.App.Services.Worker;

/// <summary>One successful <c>GetHostInfo</c>: prerequisite versions plus the AI CLIs that probe reported.</summary>
public sealed record HostInfoRead(
    HostPrerequisiteVersions Versions,
    string? CodeGraphVersion,
    string? UserProfilePath,
    IReadOnlyList<AiProviderAvailability> AiProviders);

/// <summary>A host-info read, or why it failed. A failure does not replace the last successful read.</summary>
public sealed record HostInfoQuery(HostInfoRead? Read, string? Error);

/// <summary>
/// The latest host check for the whole App. Startup, the Worker page and an Install Now all refresh it.
/// Commit and New Pull Request only read it, so opening them does not probe the AI CLIs again.
/// </summary>
public sealed class HostInfoCache(ILogger<HostInfoCache> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HostInfoRead? _current;

    /// <param name="refresh">Ask the Worker again. False returns the last successful read, or loads one when startup has not finished.</param>
    public async Task<HostInfoQuery> GetAsync(IWorkerBridge workerBridge, bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && _current is { } cached)
            return new HostInfoQuery(cached, null);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _current is { } again)
                return new HostInfoQuery(again, null);

            var query = await QueryAsync(workerBridge, cancellationToken);
            if (query.Read is not null)
                _current = query.Read;
            return query.Read is null && _current is not null && !refresh
                ? new HostInfoQuery(_current, null)
                : query;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads a GetHostInfo payload. Null when the payload is not that response.</summary>
    public static HostInfoRead? TryRead(object? data)
    {
        var dto = WorkerResponseJson.DeserializeWorkerResponse<HostInfoDto>(data);
        if (dto is null)
            return null;

        return new HostInfoRead(
            new HostPrerequisiteVersions(
                dto.DotnetVersion,
                dto.GitVersion,
                dto.GitVersionToolVersion,
                dto.CursorAgentVersion,
                dto.ClaudeVersion,
                dto.CodexVersion),
            dto.CodeGraphVersion,
            dto.UserProfilePath,
            ProvidersFrom(dto));
    }

    private async Task<HostInfoQuery> QueryAsync(IWorkerBridge workerBridge, CancellationToken cancellationToken)
    {
        var unavailable = workerBridge.GetUnavailableReason();
        if (unavailable is not null)
            return new HostInfoQuery(null, unavailable);

        try
        {
            var response = await workerBridge.SendCommandAsync(WorkerHubMethods.GetHostInfo, new { }, cancellationToken);
            if (!response.Success || response.Data is null)
                return new HostInfoQuery(null, response.Error ?? "Failed to load host info.");

            var read = TryRead(response.Data);
            if (read is null)
            {
                logger.LogWarning("GetHostInfo response could not be read.");
                return new HostInfoQuery(null, "Failed to parse host info.");
            }

            return new HostInfoQuery(read, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "GetHostInfo failed");
            return new HostInfoQuery(null, "Failed to load host info.");
        }
    }

    private static IReadOnlyList<AiProviderAvailability> ProvidersFrom(HostInfoDto dto)
    {
        if (dto.AiProviders is { Count: > 0 })
        {
            return AiProviderIds.All.Select(id =>
            {
                var entry = dto.AiProviders.FirstOrDefault(p => string.Equals(p.ProviderId, id, StringComparison.Ordinal));
                return entry is null
                    ? Missing(id)
                    : new AiProviderAvailability(id, entry.IsInstalled, entry.Version, entry.HasNoTools, entry.Problem);
            }).ToList();
        }

        // A Worker from before AI CLIs were part of GetHostInfo still reports the Cursor Agent version.
        return AiProviderIds.All.Select(id =>
        {
            var version = id switch
            {
                AiProviderIds.Claude => dto.ClaudeVersion,
                AiProviderIds.Codex => dto.CodexVersion,
                AiProviderIds.Cursor => dto.CursorAgentVersion,
                _ => null,
            };
            var installed = !HostPrerequisiteState.IsMissing(version);
            return new AiProviderAvailability(
                id,
                installed,
                installed ? version : null,
                id != AiProviderIds.Cursor,
                installed ? null : $"{AiProviderIds.DisplayName(id)} CLI was not found on the Worker's PATH.");
        }).ToList();
    }

    private static AiProviderAvailability Missing(string providerId) =>
        new(providerId, false, null, providerId != AiProviderIds.Cursor, $"{AiProviderIds.DisplayName(providerId)} CLI was not found on the Worker's PATH.");

    private sealed class HostInfoDto
    {
        public string? DotnetVersion { get; set; }
        public string? GitVersion { get; set; }
        public string? GitVersionToolVersion { get; set; }
        public string? CodeGraphVersion { get; set; }
        public string? CursorAgentVersion { get; set; }
        public string? ClaudeVersion { get; set; }
        public string? CodexVersion { get; set; }
        public string? UserProfilePath { get; set; }
        public List<AiProviderDto>? AiProviders { get; set; }
    }

    private sealed class AiProviderDto
    {
        public string? ProviderId { get; set; }
        public bool IsInstalled { get; set; }
        public string? Version { get; set; }
        public bool HasNoTools { get; set; }
        public string? Problem { get; set; }
    }
}
