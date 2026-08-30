namespace GrayMoon.App.Models;

/// <summary>A GitHub pull request merge method. Only methods the repository actually permits (per GitHubRepositoryMergeSettingsDto) are ever offered.</summary>
public enum MergeMethod
{
    Merge,
    Squash,
    Rebase
}

public static class MergeMethodExtensions
{
    /// <summary>Maps to the `merge_method` value accepted by PUT /repos/{owner}/{repo}/pulls/{pull_number}/merge.</summary>
    public static string ToGitHubValue(this MergeMethod method) => method switch
    {
        MergeMethod.Squash => "squash",
        MergeMethod.Rebase => "rebase",
        _ => "merge"
    };

    /// <summary>Button label matching GitHub's own wording for each merge method.</summary>
    public static string ToDisplayLabel(this MergeMethod method) => method switch
    {
        MergeMethod.Squash => "Squash and merge",
        MergeMethod.Rebase => "Rebase and merge",
        _ => "Create a merge commit"
    };

    /// <summary>Short explanation of what the method does to the branch's commits, for the merge-method dropdown.</summary>
    public static string ToDescription(this MergeMethod method) => method switch
    {
        MergeMethod.Squash => "All commits from this branch will be combined into one commit in the base branch.",
        MergeMethod.Rebase => "All commits from this branch will be rebased and added to the base branch.",
        _ => "All commits from this branch will be added to the base branch via a merge commit."
    };
}

/// <summary>Result of a merge attempt. Message is always GitHub's own error text on failure - GrayMoon never invents a reason.</summary>
public sealed record MergeResult(bool Success, string? Message);
