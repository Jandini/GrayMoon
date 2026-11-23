namespace GrayMoon.Application.Repositories.Policy;

/// <summary>
/// The one short sentence that tells the user how many repositories a bulk action left out, for example
/// "Skipped 3 🔒 read only repos." Counts only: the grid already marks each repository. Information, not an error and
/// not a warning: the action did what it was asked to do with the repositories it may touch.
/// </summary>
public static class RepositorySkipSummary
{
    /// <summary>The padlock the grid shows after the name of a read only repository.</summary>
    public const string Padlock = "\U0001F512";

    /// <summary>Null when nothing was skipped.</summary>
    public static string? Describe(IReadOnlyList<RepositorySkip>? skips)
    {
        if (skips is not { Count: > 0 })
            return null;

        var distinct = skips.DistinctBy(skip => skip.RepositoryId).ToList();
        var readOnly = distinct.Count(skip => IsReference(skip.Reason));
        var onTag = distinct.Count(skip => skip.Reason == RepositoryOperationReason.OnTag);
        var archived = distinct.Count(skip => skip.Reason == RepositoryOperationReason.Archived);
        var other = distinct.Count - readOnly - onTag - archived;

        var parts = new List<string>();
        Add(parts, readOnly, n => $"{n} {Padlock} read only {Repos(n)}");
        Add(parts, onTag, n => $"{n} {Repos(n)} on a tag");
        Add(parts, archived, n => $"{n} archived {Repos(n)}");
        Add(parts, other, n => $"{n} {Repos(n)}");

        var list = parts.Count == 1
            ? parts[0]
            : $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}";
        return $"Skipped {list}.";
    }

    private static bool IsReference(RepositoryOperationReason reason) =>
        reason is RepositoryOperationReason.Reference or RepositoryOperationReason.ReferenceSnapshot;

    private static string Repos(int count) => count == 1 ? "repo" : "repos";

    private static void Add(List<string> parts, int count, Func<int, string> describe)
    {
        if (count > 0)
            parts.Add(describe(count));
    }
}
