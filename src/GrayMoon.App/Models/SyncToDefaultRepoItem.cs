namespace GrayMoon.App.Models;

/// <summary>Represents a repository shown in the Sync to Default options dialog.</summary>
public sealed record SyncToDefaultRepoItem(
    string RepoName,
    string BranchName,
    bool HasRemote,
    string? PrState,
    int CommitsAhead);
