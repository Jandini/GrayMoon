namespace GrayMoon.App.Models;

public class RepoGitVersionInfo
{
    public string Version { get; init; } = "-";
    public string Branch { get; init; } = "-";
    public int? Projects { get; init; }
    /// <summary>Full project list from sync (for merge persistence).</summary>
    public IReadOnlyList<SyncProjectInfo>? ProjectsDetail { get; init; }
}
