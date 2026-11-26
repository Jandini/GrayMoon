using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>
/// Holds back, or releases, the Checkout syncs that the Worker keeps while worktrees are created. No path: the hold is
/// named by <see cref="Key"/> and covers every held sync.
/// </summary>
public sealed class HoldWorktreeHookSyncsRequest
{
    /// <summary>Name of the hold, chosen by the caller (Create Feature uses the Feature context id).</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>True to hold, false to release.</summary>
    [JsonPropertyName("hold")]
    public bool Hold { get; set; }
}
