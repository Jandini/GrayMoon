using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Services.Ai;
using GrayMoon.Common.Ai;

namespace GrayMoon.App.Services.Worker;

/// <summary>One successful <c>GetHostInfo</c>: prerequisite versions plus the AI CLIs that probe reported.</summary>
public sealed record HostInfoRead(
    HostPrerequisiteVersions Versions,
    string? CodeGraphVersion,
    string? UserProfilePath,
    IReadOnlyList<AiProviderAvailability> AiProviders)
{
    /// <summary>True when the Worker reported a CodeGraph CLI. CodeGraph is not a host prerequisite.</summary>
    public bool IsCodeGraphInstalled => !HostPrerequisiteState.IsMissing(CodeGraphVersion);
}

/// <summary>A host-info read, or why it failed. A failure does not replace the last successful read.</summary>
public sealed record HostInfoQuery(HostInfoRead? Read, string? Error);

/// <summary>
/// The latest host check for the whole App. Startup, the Worker page and an Install Now all refresh it.
/// Commit and New Pull Request only read it, so opening them does not probe the AI CLIs again.
/// </summary>
/// <remarks>
/// The first successful check of this App process also lists, on the Worker page's Environment tab, the known agent
/// variables the user already has set on the host (<see cref="AgentEnvironmentStore.ImportHostValuesAsync"/>).
/// </remarks>
public sealed class HostInfoCache(ILogger<HostInfoCache> logger, AgentEnvironmentStore? agentEnvironment = null)
{
    private bool _hostEnvironmentImported;

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
            {
                _current = query.Read;
                await ImportHostEnvironmentOnceAsync(query.Read, cancellationToken);
            }
            return query.Read is null && _current is not null && !refresh
                ? new HostInfoQuery(_current, null)
                : query;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Called inside the gate. Once per App process, at the startup host check (or the first one that succeeds when the
    /// Worker was offline then): later checks do not bring back a row the user removed. A failure never fails the host
    /// check; it is logged and the next check tries again.
    /// </summary>
    private async Task ImportHostEnvironmentOnceAsync(HostInfoRead read, CancellationToken cancellationToken)
    {
        if (_hostEnvironmentImported || agentEnvironment is null)
            return;

        try
        {
            var added = await agentEnvironment.ImportHostValuesAsync(read.AiProviders.SelectMany(p => p.HostEnvironmentValues).ToList(), cancellationToken);
            _hostEnvironmentImported = true;
            if (added.Count > 0)
                logger.LogInformation("Listed environment variables already set on the Worker host: {Variables}", string.Join(", ", added.Select(v => $"{v.Scope}:{v.Name}")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The agent environment variables set on the Worker host could not be listed; the next host check tries again.");
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
                    : new AiProviderAvailability(id, entry.IsInstalled, entry.Version, entry.HasNoTools, entry.Problem)
                    {
                        KnownEnvironmentVariables = KnownVariablesFrom(id, entry),
                        HostEnvironmentValues = HostValuesFrom(id, entry),
                    };
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

    /// <summary>The Worker is not trusted to send usable names: an entry that is not a valid variable is left out.</summary>
    private static IReadOnlyList<KnownAgentEnvironmentVariable> KnownVariablesFrom(string providerId, AiProviderDto entry) =>
        entry.KnownEnvironmentVariables is not { Count: > 0 } known
            ? []
            : known
                .Where(v => AgentEnvironment.NameError(v.Name) is null && AgentEnvironment.ValueError(v.Value) is null)
                .Select(v => new KnownAgentEnvironmentVariable(providerId, v.Name!, v.Value ?? string.Empty, v.Description?.Trim() ?? string.Empty))
                .ToList();

    /// <summary>The known variables that are set on the host, for an installed CLI only. Unusable names and values are left out.</summary>
    private static IReadOnlyList<AgentEnvironmentHostValue> HostValuesFrom(string providerId, AiProviderDto entry) =>
        !entry.IsInstalled || entry.KnownEnvironmentVariables is not { Count: > 0 } known
            ? []
            : known
                .Where(v => AgentEnvironment.NameError(v.Name) is null
                            && !string.IsNullOrEmpty(v.CurrentValue)
                            && AgentEnvironment.ValueError(v.CurrentValue) is null)
                .Select(v => new AgentEnvironmentHostValue(providerId, v.Name!, v.CurrentValue!))
                .ToList();

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
        public List<KnownVariableDto>? KnownEnvironmentVariables { get; set; }
    }

    private sealed class KnownVariableDto
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
        public string? Description { get; set; }
        public string? CurrentValue { get; set; }
    }
}
