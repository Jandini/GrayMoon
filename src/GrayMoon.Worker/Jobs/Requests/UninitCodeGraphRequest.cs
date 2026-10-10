using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>Removes the CodeGraph index from a Feature root. Sent by Remove Feature before any worktree is removed.</summary>
public sealed class UninitCodeGraphRequest
{
    /// <summary>The Feature root holding the <c>.codegraph</c> index.</summary>
    [JsonPropertyName("root")]
    public string? Root { get; set; }
}
