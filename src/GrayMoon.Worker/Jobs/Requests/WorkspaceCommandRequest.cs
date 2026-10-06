using System.Text.Json.Serialization;
using GrayMoon.Abstractions.Workspaces;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>
/// Base class for all commands that operate on workspace paths.
/// The app fills <see cref="WorkspaceRoot"/> before sending so the worker
/// never needs its own workspace-root configuration.
/// </summary>
public abstract class WorkspaceCommandRequest
{
    [JsonPropertyName("workspaceRoot")]
    public string? WorkspaceRoot { get; set; }

    /// <summary>
    /// Name of the repository whose working tree is the context root itself (the Workspace-role
    /// repository). Null when the Workspace has no Workspace repository. An old App never sends
    /// this and every repository then resolves to a subfolder, which is today's behaviour.
    /// </summary>
    [JsonPropertyName("workspaceRepositoryName")]
    public string? WorkspaceRepositoryName { get; set; }

    /// <summary>Optional. Max parallel operations for this request (e.g. repo discovery, csproj parsing). When set by the app, worker uses it; otherwise uses a default (e.g. 8).</summary>
    [JsonPropertyName("maxParallelOperations")]
    public int? MaxParallelOperations { get; set; }

    /// <summary>
    /// Optional. Which optional enrichment steps the workspace's profile activates on top of pure git
    /// synchronization. Null - an app that predates workspace profiles - means "not stated".
    /// </summary>
    [JsonPropertyName("capabilities")]
    public RepositoryOperationCapabilities? Capabilities { get; set; }

    /// <summary>
    /// <see cref="Capabilities"/> with the compatibility fallback applied: when nothing was stated the
    /// worker keeps its full pre-profile enrichment rather than silently skipping work for an existing
    /// .NET workspace.
    /// </summary>
    [JsonIgnore]
    public RepositoryOperationCapabilities EffectiveCapabilities
        => Capabilities ?? RepositoryOperationCapabilities.LegacyFullEnrichment;
}
