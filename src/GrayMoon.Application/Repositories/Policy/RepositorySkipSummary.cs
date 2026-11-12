namespace GrayMoon.Application.Repositories.Policy;

/// <summary>
/// The one sentence that tells the user which repositories a bulk action left out. Information, not an error and not
/// a warning: the action did what it was asked to do with the repositories it may touch.
/// </summary>
public static class RepositorySkipSummary
{
    private const int NamedRepositories = 5;

    /// <summary>Null when nothing was skipped.</summary>
    public static string? Describe(IReadOnlyList<RepositorySkip>? skips)
    {
        if (skips is not { Count: > 0 })
            return null;

        var distinct = skips.DistinctBy(skip => skip.RepositoryId).ToList();
        var parts = new List<string>();
        Add(parts, distinct.Where(skip => IsReference(skip.Reason)).ToList(), "read-only repository", "read-only repositories");
        Add(parts, distinct.Where(skip => skip.Reason == RepositoryOperationReason.OnTag).ToList(), "repository on a tag", "repositories on a tag");
        Add(parts, distinct.Where(skip => skip.Reason == RepositoryOperationReason.Archived).ToList(), "archived repository", "archived repositories");
        Add(
            parts,
            distinct.Where(skip => !IsReference(skip.Reason)
                && skip.Reason != RepositoryOperationReason.OnTag
                && skip.Reason != RepositoryOperationReason.Archived).ToList(),
            "repository",
            "repositories");
        return string.Join(" ", parts);
    }

    private static bool IsReference(RepositoryOperationReason reason) =>
        reason is RepositoryOperationReason.Reference or RepositoryOperationReason.ReferenceSnapshot;

    private static void Add(List<string> parts, IReadOnlyList<RepositorySkip> skips, string singular, string plural)
    {
        if (skips.Count == 0)
            return;

        var names = skips.Take(NamedRepositories).Select(skip => skip.RepositoryName).ToList();
        if (skips.Count > NamedRepositories)
            names.Add($"and {skips.Count - NamedRepositories} more");
        parts.Add($"{skips.Count} {(skips.Count == 1 ? singular : plural)} skipped: {string.Join(", ", names)}.");
    }
}
