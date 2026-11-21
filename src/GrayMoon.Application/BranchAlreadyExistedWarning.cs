namespace GrayMoon.Application;

/// <summary>
/// Wording for a branch creation that found a local branch of that name already there. The existing branch was
/// checked out as it is, so it may be old or based on something else than the base the user chose. Every caller
/// (New Branch, Create Branches, Prepare Workspace) reports it with this sentence.
/// </summary>
public static class BranchAlreadyExistedWarning
{
    private const int MaxNames = 5;

    /// <summary>One sentence naming the branch and the repositories. Null when no repository had the branch already.</summary>
    public static string? Describe(string branchName, IReadOnlyCollection<string>? repositoryNames)
    {
        if (repositoryNames is not { Count: > 0 })
            return null;

        var names = repositoryNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var count = Math.Max(names.Count, 1);
        var where = count == 1
            ? names.Count == 1 ? $"in {names[0]}" : "in this repository"
            : names.Count > MaxNames
                ? $"in {count} repositories ({string.Join(", ", names.Take(MaxNames))}, and {names.Count - MaxNames} more)"
                : $"in {count} repositories ({string.Join(", ", names)})";

        return $"Branch '{branchName}' already existed {where}. It was checked out as it is and was not created from the base branch.";
    }
}
