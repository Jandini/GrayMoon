namespace GrayMoon.App.Models;

/// <summary>Represents a repository shown in the Return to Default options dialog.</summary>
public sealed record ReturnToDefaultRepoItem(
    string RepoName,
    string BranchName,
    bool HasRemote,
    string? PrState,
    int CommitsAhead);
