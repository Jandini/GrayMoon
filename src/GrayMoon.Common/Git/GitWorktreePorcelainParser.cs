namespace GrayMoon.Common.Git;

/// <summary>
/// Parses <c>git worktree list --porcelain</c> output into structured <see cref="GitWorktreeInfo"/> entries.
/// Process-free; callers supply the command output.
/// </summary>
public static class GitWorktreePorcelainParser
{
    private const string HeadsPrefix = "refs/heads/";

    public static IReadOnlyList<GitWorktreeInfo> Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var normalized = output.Replace("\r\n", "\n").Replace('\r', '\n');
        var results = new List<GitWorktreeInfo>();
        GitWorktreeInfo? current = null;

        foreach (var line in normalized.Split('\n'))
        {
            if (line.Length == 0)
            {
                if (current != null)
                {
                    results.Add(Finalize(current));
                    current = null;
                }

                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                if (current != null)
                    results.Add(Finalize(current));

                current = new GitWorktreeInfo
                {
                    WorktreePath = line["worktree ".Length..],
                };
                continue;
            }

            if (current == null)
                continue;

            if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                current.HeadSha = line["HEAD ".Length..];
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var branchRef = line["branch ".Length..];
                current.BranchRef = branchRef;
                current.BranchName = TryGetBranchName(branchRef);
                current.IsDetached = false;
            }
            else if (string.Equals(line, "detached", StringComparison.Ordinal))
            {
                current.IsDetached = true;
                current.BranchRef = null;
                current.BranchName = null;
            }
            else if (string.Equals(line, "bare", StringComparison.Ordinal))
            {
                current.IsBare = true;
            }
            else if (line.StartsWith("prunable", StringComparison.Ordinal))
            {
                current.IsPrunable = true;
                current.PrunableReason = line.Length > "prunable".Length
                    ? line["prunable".Length..].TrimStart()
                    : null;
            }
        }

        if (current != null)
            results.Add(Finalize(current));

        return results;
    }

    /// <summary>Returns the short branch name for a heads ref, otherwise null.</summary>
    public static string? TryGetBranchName(string? branchRef)
    {
        if (string.IsNullOrWhiteSpace(branchRef))
            return null;

        if (branchRef.StartsWith(HeadsPrefix, StringComparison.Ordinal))
            return branchRef[HeadsPrefix.Length..];

        return null;
    }

    private static GitWorktreeInfo Finalize(GitWorktreeInfo info)
    {
        if (info.BranchName == null && info.BranchRef != null)
            info.BranchName = TryGetBranchName(info.BranchRef);
        return info;
    }
}
