namespace GrayMoon.Application;

public sealed record OperationProgress(string Message, int? Completed = null, int? Total = null);

public sealed record OperationResult(
    bool Success,
    string? Error = null,
    IReadOnlyDictionary<int, string>? RepoErrors = null,
    IReadOnlyDictionary<int, string>? LevelErrors = null)
{
    public static OperationResult Ok(
        IReadOnlyDictionary<int, string>? repoErrors = null,
        IReadOnlyDictionary<int, string>? levelErrors = null)
        => new(true, null, repoErrors, levelErrors);

    public static OperationResult Fail(
        string error,
        IReadOnlyDictionary<int, string>? repoErrors = null,
        IReadOnlyDictionary<int, string>? levelErrors = null)
        => new(false, error, repoErrors, levelErrors);
}

/// <summary>
/// Result of a dependency-update run. <see cref="Success"/> is false when any repo or workspace-level
/// error was reported; chained push (New Feature / Update and Push) must not run in that case.
/// An empty <see cref="SyncedRepoIds"/> with <see cref="Success"/> true means nothing needed updating.
/// </summary>
public sealed record DependencyUpdateRunResult(bool Success, IReadOnlySet<int> SyncedRepoIds)
{
    public static DependencyUpdateRunResult Ok(IReadOnlySet<int>? syncedRepoIds = null)
        => new(true, syncedRepoIds ?? new HashSet<int>());

    public static DependencyUpdateRunResult Failed()
        => new(false, new HashSet<int>());

    public bool ShouldChainPush(bool pushRequested) => pushRequested && Success;
}

public static class OperationProgressExtensions
{
    public static void Report(
        this IProgress<OperationProgress>? progress,
        string message,
        int? completed = null,
        int? total = null)
        => progress?.Report(new OperationProgress(message, completed, total));

    public static Action<string> ToMessageAction(this IProgress<OperationProgress>? progress)
        => message => progress.Report(message);

    public static IProgress<OperationProgress> ToOperationProgress(this Action<string> report)
        => new Progress<OperationProgress>(p => report(p.Message));

    public static void ShowRepoErrors(this OperationResult result, Action<string> showError)
    {
        if (result.RepoErrors is { Count: > 0 })
        {
            foreach (var (id, err) in result.RepoErrors)
                showError($"{id}: {err}");
            return;
        }

        if (!result.Success && !string.IsNullOrWhiteSpace(result.Error))
            showError(result.Error);
    }
}
