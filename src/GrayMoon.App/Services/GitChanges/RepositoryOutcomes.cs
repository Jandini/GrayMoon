using System.Collections.Concurrent;

namespace GrayMoon.App.Services.GitChanges;

public enum RepositoryOutcomeKind
{
    Succeeded,

    /// <summary>The repository had nothing to do (for example no changes to commit). Not an error, but always named in the summary.</summary>
    Skipped,

    Failed,
}

public sealed record RepositoryOutcome(int Key, string Repository, RepositoryOutcomeKind Kind, string? Detail);

/// <summary>
/// One result per repository for a multi-repository operation (Commit, Stage All, Unstage All, stage selected).
/// Every repository the operation set out to handle is expected to record exactly one outcome. A repository that
/// records nothing (an exception path that was missed, or a cancelled run) is reported as failed rather than
/// silently dropped, so a repository can never disappear from the summary.
/// Thread safe: the operations fan out in parallel.
/// </summary>
public sealed class RepositoryOutcomes
{
    private const int MaxNamed = 6;

    private readonly IReadOnlyList<(int Key, string Repository)> _expected;
    private readonly ConcurrentDictionary<int, RepositoryOutcome> _outcomes = new();

    public RepositoryOutcomes(IEnumerable<(int Key, string Repository)> expected)
    {
        _expected = expected.ToList();
    }

    public int Expected => _expected.Count;

    public void Succeeded(int key, string repository) =>
        Record(new RepositoryOutcome(key, repository, RepositoryOutcomeKind.Succeeded, null));

    public void Skipped(int key, string repository, string detail) =>
        Record(new RepositoryOutcome(key, repository, RepositoryOutcomeKind.Skipped, detail));

    public void Failed(int key, string repository, string detail) =>
        Record(new RepositoryOutcome(key, repository, RepositoryOutcomeKind.Failed, detail));

    // The first outcome wins. A later duplicate must never turn a failure into a success.
    private void Record(RepositoryOutcome outcome) => _outcomes.TryAdd(outcome.Key, outcome);

    /// <summary>All outcomes in the order the repositories were requested; expected repositories with no outcome are failed.</summary>
    public IReadOnlyList<RepositoryOutcome> Complete()
    {
        var result = new List<RepositoryOutcome>(_expected.Count);
        foreach (var (key, repository) in _expected)
        {
            result.Add(_outcomes.TryGetValue(key, out var outcome)
                ? outcome
                : new RepositoryOutcome(key, repository, RepositoryOutcomeKind.Failed, "No result was recorded for this repository."));
        }

        return result;
    }

    public static int Count(IReadOnlyList<RepositoryOutcome> outcomes, RepositoryOutcomeKind kind) =>
        outcomes.Count(o => o.Kind == kind);

    public static bool HasFailures(IReadOnlyList<RepositoryOutcome> outcomes) =>
        outcomes.Any(o => o.Kind == RepositoryOutcomeKind.Failed);

    /// <summary>One line for the toast and the job terminal, for example "Committed in 25 of 26 repositories. Not committed (1): Edx1.File.Email (reason)."</summary>
    public static string Summarize(IReadOnlyList<RepositoryOutcome> outcomes, string pastVerb, string notVerb)
    {
        var succeeded = Count(outcomes, RepositoryOutcomeKind.Succeeded);
        var skipped = outcomes.Where(o => o.Kind == RepositoryOutcomeKind.Skipped).ToList();
        var failed = outcomes.Where(o => o.Kind == RepositoryOutcomeKind.Failed).ToList();

        var text = failed.Count == 0 && skipped.Count == 0
            ? $"{pastVerb} in {succeeded} {Noun(succeeded)}."
            : $"{pastVerb} in {succeeded} of {outcomes.Count} {Noun(outcomes.Count)}.";

        if (failed.Count > 0)
        {
            text += $" {notVerb} ({failed.Count}): {Named(failed, withDetail: true)}.";
        }

        if (skipped.Count > 0)
        {
            text += $" Nothing to do in ({skipped.Count}): {Named(skipped, withDetail: false)}.";
        }

        return text;
    }

    private static string Noun(int count) => count == 1 ? "repository" : "repositories";

    private static string Named(IReadOnlyList<RepositoryOutcome> outcomes, bool withDetail)
    {
        var shown = outcomes
            .Take(MaxNamed)
            .Select(o => withDetail && !string.IsNullOrWhiteSpace(o.Detail) ? $"{o.Repository} ({Trim(o.Detail)})" : o.Repository);
        var more = outcomes.Count > MaxNamed ? $" and {outcomes.Count - MaxNamed} more" : string.Empty;
        return string.Join(", ", shown) + more;
    }

    private static string Trim(string detail)
    {
        var line = detail.Trim().ReplaceLineEndings(" ");
        return line.Length <= 140 ? line : line[..137] + "...";
    }
}
