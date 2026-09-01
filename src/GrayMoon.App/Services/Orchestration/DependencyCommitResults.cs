namespace GrayMoon.App.Services.Orchestration;

internal readonly record struct DependencyCommitClassification(
    IReadOnlyList<int> CommittedRepoIds,
    IReadOnlyList<(int RepoId, string Error)> Errors);

/// <summary>
/// Interprets StageAndCommit results after the orchestrator has already written files:
/// every repo that was supposed to commit must actually commit, and every error is kept
/// (the caller must not stop at the first one).
/// </summary>
internal static class DependencyCommitResults
{
    public const string NotCommittedError = "Changes were written but not committed.";

    public static DependencyCommitClassification Classify(
        IReadOnlyList<(int RepoId, bool Committed, string? ErrorMessage)> results,
        IReadOnlySet<int>? requireCommittedRepoIds = null)
    {
        var required = requireCommittedRepoIds
            ?? results.Select(r => r.RepoId).ToHashSet();
        var committed = new List<int>();
        var errors = new List<(int RepoId, string Error)>();

        foreach (var (repoId, wasCommitted, errMsg) in results)
        {
            if (!string.IsNullOrEmpty(errMsg))
            {
                errors.Add((repoId, errMsg));
                continue;
            }

            if (!wasCommitted)
            {
                if (required.Contains(repoId))
                    errors.Add((repoId, NotCommittedError));
                continue;
            }

            committed.Add(repoId);
        }

        return new DependencyCommitClassification(committed, errors);
    }
}
