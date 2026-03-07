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

    /// <summary>Optional. Max parallel operations for this request (e.g. repo discovery, csproj parsing). When set by the app, agent uses it; otherwise uses a default (e.g. 8).</summary>
    [JsonPropertyName("maxParallelOperations")]
    public int? MaxParallelOperations { get; set; }
}
