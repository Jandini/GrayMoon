namespace GrayMoon.App.Services.Workspaces;

/// <summary>Maps raw git/agent push stderr into a short user-facing message.</summary>
internal static class PushErrorFormatter
{
    public static string Format(string? rawError)
    {
        var err = rawError ?? "Push failed";
        if (IsArchivedRejection(err))
            return "Push rejected: this repository is archived on GitHub and is read-only.";
        // Large-file / LFS rejections often include "pre-receive hook declined" — detect them
        // before protected-branch so GH001 is not mislabeled as branch protection.
        if (IsLargeFileRejection(err))
            return FormatLargeFileRejection(err);
        if (IsProtectedBranchRejection(err))
            return "Push rejected: the remote branch is protected. Use a pull request or update branch protection rules to push directly.";
        if (IsMergeConflictError(err))
            return "Push skipped: merge conflict while pulling remote changes. Resolve conflicts and retry.";
        if (IsPullFailureError(err))
            return "Push skipped: could not pull remote changes. Check repository state and retry.";
        if (IsNonFastForwardRejection(err))
            return "Push rejected: remote has new commits. Fetching latest state - pull and retry.";
        // Generic hook declines (and anything else unrecognized): echo remote detail rather than
        // claiming branch protection.
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

    public static bool IsLargeFileRejection(string? err) =>
        err != null &&
        (err.Contains("GH001", StringComparison.OrdinalIgnoreCase)
         || err.Contains("Large files detected", StringComparison.OrdinalIgnoreCase)
         || err.Contains("Git Large File Storage", StringComparison.OrdinalIgnoreCase)
         || err.Contains("exceeds GitHub's file size limit", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Protected-branch only. Deliberately omits bare "hook declined" / "pre-receive hook declined"
    /// — those also appear on GH001 large-file rejections and other hooks.
    /// </summary>
    public static bool IsProtectedBranchRejection(string? err) =>
        err != null &&
        (err.Contains("protected branch", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("GH006", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("GH013", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("repository rule violations", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("not allowed to push code to a protected branch", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("changes must be made through a pull request", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("TF401027", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("TF402455", StringComparison.OrdinalIgnoreCase));

    private static string FormatLargeFileRejection(string err)
    {
        var detailParts = new List<string>();
        foreach (var rawLine in err.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = StripRemotePrefixes(rawLine.Trim());
            if (line.Length == 0)
                continue;
            // Prefer the concrete File/size lines GitHub emits (e.g. "File big.bin is 120.00 MB; this exceeds...").
            if (line.StartsWith("File ", StringComparison.OrdinalIgnoreCase)
                && (line.Contains("exceeds", StringComparison.OrdinalIgnoreCase)
                    || line.Contains(" MB", StringComparison.OrdinalIgnoreCase)))
            {
                detailParts.Add(line.TrimEnd('.'));
            }
        }

        if (detailParts.Count > 0)
            return "Push rejected: file too large for GitHub. " + string.Join(" ", detailParts);

        return "Push rejected: a file exceeds GitHub's size limit. Use Git LFS or reduce the file size.";
    }

    private static string StripRemotePrefixes(string line)
    {
        while (true)
        {
            if (line.StartsWith("remote:", StringComparison.OrdinalIgnoreCase))
            {
                line = line["remote:".Length..].TrimStart();
                continue;
            }
            if (line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                line = line["error:".Length..].TrimStart();
                continue;
            }
            return line;
        }
    }
}
