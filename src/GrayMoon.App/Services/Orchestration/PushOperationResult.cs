namespace GrayMoon.App.Services.Orchestration;

/// <summary>Maps collected per-repo push errors onto <see cref="OperationResult"/>.</summary>
internal static class PushOperationResult
{
    public const string StoppedAfterFailures = "Push stopped after repository failure(s).";

    public static OperationResult FromRepoErrors(IReadOnlyDictionary<int, string> repoErrors)
    {
        if (repoErrors.Count == 0)
            return OperationResult.Ok();

        return OperationResult.Fail(StoppedAfterFailures, new Dictionary<int, string>(repoErrors));
    }

    public static bool CanProceedToNextLevel(int failedPushCount) => failedPushCount == 0;
}
