namespace GrayMoon.App.Services.Orchestration;

/// <summary>Maps collected per-repo and per-level push errors onto <see cref="OperationResult"/>.</summary>
internal static class PushOperationResult
{
    public const string StoppedAfterFailures = "Push stopped after repository failure(s).";

    public static OperationResult FromRepoErrors(IReadOnlyDictionary<int, string> repoErrors)
        => FromErrors(repoErrors, new Dictionary<int, string>());

    public static OperationResult FromErrors(
        IReadOnlyDictionary<int, string> repoErrors,
        IReadOnlyDictionary<int, string> levelErrors)
    {
        if (repoErrors.Count == 0 && levelErrors.Count == 0)
            return OperationResult.Ok();

        return OperationResult.Fail(
            StoppedAfterFailures,
            repoErrors.Count > 0 ? new Dictionary<int, string>(repoErrors) : null,
            levelErrors.Count > 0 ? new Dictionary<int, string>(levelErrors) : null);
    }

    /// <summary>
    /// One-line account of why a failed push stopped, for the message a user sees when the run ends: the lowest
    /// failed level and its error (a package-wait timeout is reported there), else the result's own error.
    /// </summary>
    public static string Describe(OperationResult failed)
    {
        if (failed.LevelErrors is { Count: > 0 } levelErrors)
        {
            var (level, message) = levelErrors.OrderBy(kv => kv.Key).First();
            var firstLine = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? message;
            return level > 0 ? $"Push stopped at level {level}: {firstLine}" : $"Push stopped: {firstLine}";
        }

        return string.IsNullOrWhiteSpace(failed.Error) ? StoppedAfterFailures : failed.Error;
    }

    public static bool CanProceedToNextLevel(int failedPushCount) => failedPushCount == 0;
}
