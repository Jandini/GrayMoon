namespace GrayMoon.Agent;

public class AgentOptions
{
    public const string SectionName = "GrayMoon";

    public string AppHubUrl { get; set; } = "http://host.docker.internal:8384/hub/agent";
    /// <summary>Base URL for calling the GrayMoon App HTTP API (no trailing slash), e.g. "http://host.docker.internal:8384".</summary>
    public string? AppApiBaseUrl { get; set; }
    public int ListenPort { get; set; } = 9191;
    public int MaxConcurrentCommands { get; set; } = Environment.ProcessorCount * 2;

    /// <summary>
    /// Worker count for the dedicated read-only command pool (GetGitChangeStatus). Kept
    /// small and separate from <see cref="MaxConcurrentCommands"/> so reads stay responsive even when the
    /// main pool is fully occupied by long-running writes (push/update/sync).
    /// </summary>
    public int MaxConcurrentReadCommands { get; set; } = 8;

    /// <summary>
    /// Worker count for the dedicated diff command pool (GetGitFileDiff). Kept separate from both
    /// <see cref="MaxConcurrentCommands"/> and <see cref="MaxConcurrentReadCommands"/> so opening a diff
    /// never queues behind a workspace status rescan (which can fan out many GetGitChangeStatus calls) or
    /// any other write/read command.
    /// </summary>
    public int MaxConcurrentDiffCommands { get; set; } = 4;
}
