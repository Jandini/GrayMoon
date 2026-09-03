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

    public static bool CanProceedToNextLevel(int failedPushCount) => failedPushCount == 0;
}
