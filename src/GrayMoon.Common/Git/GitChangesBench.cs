using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Common.Git;

/// <summary>
/// Permanent structured Git Changes scale logs. Message text always starts with <c>GitChangesBench</c>
/// so support can filter one prefix. Named properties only - never path names or file contents.
/// Information when path count or elapsed time meets the floors in <see cref="GitChangesOptions"/>;
/// Debug otherwise.
/// </summary>
public static class GitChangesBench
{
    public const string MessagePrefix = "GitChangesBench";

    /// <summary>Matches Worker/App SignalR camelCase JSON closely enough for payload size estimates.</summary>
    public static readonly JsonSerializerOptions PayloadSizeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static bool MeetsInformationFloor(GitChangesOptions options, int pathCount, long elapsedMs) =>
        pathCount >= options.BenchmarkLogMinPathCount
        || elapsedMs >= options.BenchmarkLogMinElapsedMs;

    public static LogLevel Level(GitChangesOptions options, int pathCount, long elapsedMs) =>
        MeetsInformationFloor(options, pathCount, elapsedMs) ? LogLevel.Information : LogLevel.Debug;

    /// <summary>
    /// UTF-8 byte length of <paramref name="value"/> as camelCase JSON. Returns 0 when serialization fails.
    /// Call only when the size is needed for a log that will actually be written.
    /// </summary>
    public static int EstimateUtf8JsonBytes<T>(T value)
    {
        try
        {
            var json = JsonSerializer.Serialize(value, PayloadSizeJsonOptions);
            return Encoding.UTF8.GetByteCount(json);
        }
        catch
        {
            return 0;
        }
    }

    public static int BytesPerEntry(int payloadUtf8Bytes, int pathCount) =>
        pathCount <= 0 ? 0 : payloadUtf8Bytes / pathCount;

    public static void Log(
        ILogger logger,
        GitChangesOptions options,
        string eventName,
        int pathCount,
        long elapsedMs,
        params (string Name, object? Value)[] properties)
    {
        var level = Level(options, pathCount, elapsedMs);
        if (!logger.IsEnabled(level))
            return;

        // Build a single template with named holes so Serilog/MEL keep structured properties.
        var sb = new StringBuilder(MessagePrefix.Length + eventName.Length + 64 + properties.Length * 24);
        sb.Append(MessagePrefix).Append(' ').Append(eventName);
        var values = new object?[properties.Length];
        for (var i = 0; i < properties.Length; i++)
        {
            sb.Append(' ').Append(properties[i].Name).Append("={").Append(properties[i].Name).Append('}');
            values[i] = properties[i].Value;
        }

        logger.Log(level, sb.ToString(), values);
    }

    public static long ElapsedMs(long startTimestamp) =>
        (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
