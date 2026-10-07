namespace GrayMoon.Worker.Services;

/// <summary>
/// The rule for which remote-tracking branch counts as the repository's default, kept in one place so the
/// ref snapshot and the stand-alone lookup cannot drift apart. It is exactly the order the one-probe-per-step
/// lookup used: where <c>refs/remotes/origin/HEAD</c> points, when that branch exists, then
/// <c>origin/main</c>, then <c>origin/master</c>.
/// </summary>
internal static class OriginDefaultRef
{
    private const string RemotePrefix = "refs/remotes/origin/";

    /// <summary>
    /// The branch name <c>refs/remotes/origin/HEAD</c> points at, or null when it is not a symbolic ref, points
    /// outside <c>origin</c>, or points at <c>HEAD</c> itself.
    /// </summary>
    public static string? HeadTargetBranch(string? symref)
    {
        if (string.IsNullOrWhiteSpace(symref))
            return null;

        var trimmed = symref.Trim();
        if (!trimmed.StartsWith(RemotePrefix, StringComparison.Ordinal))
            return null;

        var branch = trimmed[RemotePrefix.Length..];
        return string.IsNullOrEmpty(branch) || branch == "HEAD" ? null : branch;
    }

    /// <summary>Picks <c>origin/&lt;branch&gt;</c> from what exists on the remote, or null when nothing qualifies.</summary>
    /// <param name="headTarget">Result of <see cref="HeadTargetBranch"/>.</param>
    /// <param name="remoteBranchExists">Whether <c>refs/remotes/origin/&lt;name&gt;</c> exists.</param>
    public static string? Pick(string? headTarget, Func<string, bool> remoteBranchExists)
    {
        if (headTarget != null && remoteBranchExists(headTarget))
            return $"origin/{headTarget}";
        if (remoteBranchExists("main"))
            return "origin/main";
        if (remoteBranchExists("master"))
            return "origin/master";
        return null;
    }

    /// <summary>
    /// Builds <c>origin/&lt;branch&gt;</c> for an ahead/behind comparison base. Returns null when
    /// <paramref name="branchName"/> is null/whitespace. Accepts a name already prefixed with <c>origin/</c>.
    /// </summary>
    public static string? ToOriginBranchRef(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return null;
        var trimmed = branchName.Trim();
        if (trimmed.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return $"origin/{trimmed}";
    }

    /// <summary>A ref name that survives being put into an argument string: no spaces, quotes, glob characters or leading dash.</summary>
    public static bool IsPlainRefName(string name)
        => name.Length > 0
           && name[0] != '-'
           && name.IndexOfAny([' ', '\t', '\r', '\n', '"', '\'', '*', '?', '[', '\\']) < 0;
}
