namespace GrayMoon.App.Models;

/// <summary>Represents a repository listed in the default-branch warning dialog.</summary>
public sealed record DefaultBranchWarningItem(string RepoName, string DefaultBranchName);
