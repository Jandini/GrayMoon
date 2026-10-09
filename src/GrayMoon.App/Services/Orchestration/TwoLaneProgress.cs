namespace GrayMoon.App.Services.Orchestration;

/// <summary>
/// Encodes the two lanes of an update-and-push run into the single overlay message string. The lanes are joined
/// with <see cref="Separator"/> on its own line, so the overlay can split them before parsing each lane with its
/// usual "primary line, then level / countdown lines" rules. One lane alone is just that lane's message.
/// </summary>
public static class OverlayLanes
{
    /// <summary>ASCII record separator: never appears in a progress message, so it cannot be confused with text.</summary>
    public const char Separator = '\u001E';

    public static string Join(string? first, string? second)
    {
        var hasFirst = !string.IsNullOrWhiteSpace(first);
        var hasSecond = !string.IsNullOrWhiteSpace(second);
        if (hasFirst && hasSecond)
            return $"{first!.Trim()}\n{Separator}\n{second!.Trim()}";
        return hasFirst ? first!.Trim() : hasSecond ? second!.Trim() : string.Empty;
    }

    /// <summary>The non-empty lanes of <paramref name="message"/>, in order. A message without a separator is one lane.</summary>
    public static IReadOnlyList<string> Split(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return [];
        return message
            .Split(Separator)
            .Select(lane => lane.Trim())
            .Where(lane => lane.Length > 0)
            .ToList();
    }
}

/// <summary>
/// Keeps the latest message of the update lane and of the push lane and publishes them together as one overlay
/// message. Both lanes report from thread-pool threads, so every change is serialized and published in order.
/// </summary>
public sealed class TwoLaneProgress(Action<string> report)
{
    private readonly object _gate = new();
    private string? _update;
    private string? _push;

    public Action<string> Update => message => Set(ref _update, message);

    public Action<string> Push => message => Set(ref _push, message);

    /// <summary>Drops the update line once the update has finished, so only the push lane is shown.</summary>
    public void EndUpdate() => Set(ref _update, null);

    /// <summary>Drops the push line once the push lane has finished.</summary>
    public void EndPush() => Set(ref _push, null);

    private void Set(ref string? slot, string? message)
    {
        lock (_gate)
        {
            slot = message;
            var combined = OverlayLanes.Join(_update, _push);
            if (combined.Length > 0)
                report(combined);
        }
    }
}

/// <summary>
/// <see cref="IProgress{T}"/> that runs the callback on the reporting thread. <see cref="Progress{T}"/> posts to the
/// thread pool when there is no synchronization context, which can reorder messages; the lanes need them in order.
/// </summary>
public sealed class SynchronousProgress(Action<string> report) : IProgress<OperationProgress>
{
    public void Report(OperationProgress value) => report(value.Message);
}
