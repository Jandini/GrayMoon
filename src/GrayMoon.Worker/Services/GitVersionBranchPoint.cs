using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Checks that a GitVersion run with <c>/b &lt;branch&gt; /c &lt;sha&gt;</c> really versioned that branch at that commit.
/// GitVersion 5 does not fail when it cannot: with no local branch of that name it versions <c>origin/&lt;branch&gt;</c>, and
/// when the commit is not in the local branch it versions the branch tip. Both look like success, so the output's
/// <c>BranchName</c> and <c>Sha</c> are compared with what was asked.
/// </summary>
public static class GitVersionBranchPoint
{
    /// <summary>Null when <paramref name="result"/> names <paramref name="branchName"/> and <paramref name="commitSha"/>; otherwise one sentence that says what GitVersion answered instead.</summary>
    public static string? DescribeMismatch(GitVersionResult? result, string branchName, string commitSha)
    {
        if (result is null || string.IsNullOrWhiteSpace(result.InformationalVersion))
            return "GitVersion returned no version.";

        var reportedBranch = result.BranchName?.Trim() ?? "";
        if (!string.Equals(reportedBranch, branchName.Trim(), StringComparison.Ordinal))
            return $"GitVersion versioned branch '{reportedBranch}' instead of '{branchName.Trim()}' (is there a local '{branchName.Trim()}' branch?).";

        var reportedSha = result.Sha?.Trim() ?? "";
        if (!string.Equals(reportedSha, commitSha.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"GitVersion versioned commit {Short(reportedSha)} instead of {Short(commitSha)} (the local '{branchName.Trim()}' branch does not contain it).";

        return null;
    }

    private static string Short(string sha)
    {
        var trimmed = sha.Trim();
        return trimmed.Length > 7 ? trimmed[..7] : (trimmed.Length == 0 ? "(none)" : trimmed);
    }
}
