namespace GrayMoon.App.Services.Workspaces;

/// <summary>Maps raw git/agent push stderr into a short user-facing message.</summary>
internal static class PushErrorFormatter
{
    public static string Format(string? rawError)
    {
        var err = rawError ?? "Push failed";
        if (IsArchivedRejection(err))
            return "Push rejected: this repository is archived on GitHub and is read-only.";
        if (IsProtectedBranchRejection(err))
            return "Push rejected: the remote branch is protected. Use a pull request or update branch protection rules to push directly.";
        if (IsMergeConflictError(err))
            return "Push skipped: merge conflict while pulling remote changes. Resolve conflicts and retry.";
        if (IsPullFailureError(err))
            return "Push skipped: could not pull remote changes. Check repository state and retry.";
        if (IsNonFastForwardRejection(err))
            return "Push rejected: remote has new commits. Fetching latest state - pull and retry.";
        return err;
    }

    public static bool IsArchivedRejection(string? err) =>
        err != null &&
        (err.Contains("repository was archived", StringComparison.OrdinalIgnoreCase)
         || err.Contains("archived so it is read-only", StringComparison.OrdinalIgnoreCase)
         || (err.Contains("403", StringComparison.Ordinal) && err.Contains("archived", StringComparison.OrdinalIgnoreCase)));

    public static bool IsNonFastForwardRejection(string? err) =>
        err != null &&
        (err.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
         (err.Contains("[rejected]", StringComparison.OrdinalIgnoreCase) && err.Contains("fetch first", StringComparison.OrdinalIgnoreCase)));

    public static bool IsMergeConflictError(string? err) =>
        err != null && err.Contains("merge conflict", StringComparison.OrdinalIgnoreCase);

    public static bool IsPullFailureError(string? err) =>
        err != null && err.Contains("pull failed", StringComparison.OrdinalIgnoreCase);

    public static bool IsProtectedBranchRejection(string? err) =>
        err != null &&
        (err.Contains("protected branch", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("GH006", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("GH013", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("repository rule violations", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("pre-receive hook declined", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("hook declined", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("not allowed to push code to a protected branch", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("changes must be made through a pull request", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("TF401027", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("TF402455", StringComparison.OrdinalIgnoreCase));
}
