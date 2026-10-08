using System.Text.Json.Serialization;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Models;

/// <summary>
/// Result of one lock scan over several paths: one result per path in request order, and whether the handle table could be
/// read (when not, the caller falls back to Restart Manager).
/// </summary>
public sealed record LockScanOutcome(
    IReadOnlyList<FileLockInspectionResult> Results,
    bool HandleTableAvailable,
    string? HandleTableError,
    string? Timings = null);

/// <summary>stdin of the isolated <c>graymoon-worker inspect-locks</c> child process.</summary>
public sealed class LockScanRequest
{
    [JsonPropertyName("paths")]
    public List<string>? Paths { get; set; }

    /// <summary>Processes never to report (the Worker that started the child).</summary>
    [JsonPropertyName("excludeProcessIds")]
    public List<int>? ExcludeProcessIds { get; set; }

    [JsonPropertyName("budgetMilliseconds")]
    public int BudgetMilliseconds { get; set; }
}

/// <summary>stdout of the isolated <c>graymoon-worker inspect-locks</c> child process (after its marker).</summary>
public sealed class LockScanResponse
{
    [JsonPropertyName("handleTableAvailable")]
    public bool HandleTableAvailable { get; set; }

    [JsonPropertyName("handleTableError")]
    public string? HandleTableError { get; set; }

    /// <summary>Time per pass, for the Worker log (for example "cwd 40ms, handles 900ms, mapped 700ms").</summary>
    [JsonPropertyName("timings")]
    public string? Timings { get; set; }

    [JsonPropertyName("results")]
    public List<InspectPathLocksResult>? Results { get; set; }

    public static LockScanResponse From(LockScanOutcome outcome) => new()
    {
        HandleTableAvailable = outcome.HandleTableAvailable,
        HandleTableError = outcome.HandleTableError,
        Timings = outcome.Timings,
        Results = outcome.Results.Select(r => new InspectPathLocksResult
        {
            Exists = true,
            BlockingProcesses = BlockingProcessResponse.From(r.Processes),
            MayBeIncomplete = r.MayBeIncomplete,
            Diagnostic = r.Diagnostic,
        }).ToList(),
    };

    /// <summary>Back to the in-memory shape; null when the response does not have one result per requested path.</summary>
    public LockScanOutcome? ToOutcome(int expectedCount)
    {
        if (Results is null || Results.Count != expectedCount)
            return null;

        return new LockScanOutcome(
            Results.Select(r => new FileLockInspectionResult(
                (r.BlockingProcesses ?? []).Select(p => p.ToInfo()).ToList(),
                r.MayBeIncomplete,
                r.Diagnostic)).ToList(),
            HandleTableAvailable,
            HandleTableError,
            Timings);
    }
}

/// <summary>Names shared by the Worker and its isolated <c>inspect-locks</c> child process.</summary>
public static class LockScanProtocol
{
    /// <summary>The hidden CLI verb the child process runs.</summary>
    public const string Verb = "inspect-locks";

    /// <summary>Prefix of the stdout line that carries the JSON result.</summary>
    public const string OutputMarker = "GRAYMOON-LOCK-SCAN:";
}
