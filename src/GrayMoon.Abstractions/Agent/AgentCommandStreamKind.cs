namespace GrayMoon.Abstractions.Agent;

/// <summary>Kind of line streamed from <see cref="AgentHubMethods.CommandOutput"/>.</summary>
public enum AgentCommandStreamKind
{
    /// <summary>Executable and arguments echo (e.g. git …).</summary>
    CommandLine = 0,

    /// <summary>Standard output segment.</summary>
    Stdout = 1,

    /// <summary>Standard error segment.</summary>
    Stderr = 2
}
