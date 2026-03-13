namespace GrayMoon.Agent;

public class AgentOptions
{
    public const string SectionName = "GrayMoon";

    public string AppHubUrl { get; set; } = "http://host.docker.internal:8384/hub/agent";
    /// <summary>Base URL for calling the GrayMoon App HTTP API (no trailing slash), e.g. "http://host.docker.internal:8384".</summary>
    public string? AppApiBaseUrl { get; set; }
    public int ListenPort { get; set; } = 9191;
    public int MaxConcurrentCommands { get; set; } = Environment.ProcessorCount * 2;
}
