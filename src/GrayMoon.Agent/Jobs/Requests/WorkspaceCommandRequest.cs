using System.Text.Json.Serialization;

namespace GrayMoon.Agent.Jobs.Requests;

/// <summary>
/// Base class for all commands that operate on workspace paths.
/// The app fills <see cref="WorkspaceRoot"/> before sending so the agent
/// never needs its own workspace-root configuration.
/// </summary>
public abstract class WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceRoot")]
    public string? WorkspaceRoot { get; set; }
}
