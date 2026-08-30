namespace GrayMoon.App.Services.Application;

public sealed record OperationProgress(string Message, int? Completed = null, int? Total = null);

public sealed record OperationResult(
    bool Success,
    string? Error = null,
    IReadOnlyDictionary<int, string>? RepoErrors = null)
{
    public static OperationResult Ok(IReadOnlyDictionary<int, string>? repoErrors = null)
        => new(true, null, repoErrors);

    public static OperationResult Fail(string error, IReadOnlyDictionary<int, string>? repoErrors = null)
        => new(false, error, repoErrors);
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
