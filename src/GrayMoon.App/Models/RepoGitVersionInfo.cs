namespace GrayMoon.App.Models;

public class RepoGitVersionInfo
{
    public string Version { get; init; } = "-";
    public string Branch { get; init; } = "-";
    public int? Projects { get; init; }
    /// <summary>Outgoing commits (ahead of remote).</summary>
    public int? OutgoingCommits { get; init; }
    /// <summary>Incoming commits (behind remote).</summary>
    public int? IncomingCommits { get; init; }
    /// <summary>Full project list from sync (for merge persistence).</summary>
    public IReadOnlyList<SyncProjectInfo>? ProjectsDetail { get; init; }
}
