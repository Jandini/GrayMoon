using System.Text.Json.Serialization;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// App-side shape of the Agent's InspectWorktree response (see GrayMoon.Agent.Jobs.Response.InspectWorktreeResponse).
/// Only the fields this unit reads; a newer Worker's extra fields are ignored by the deserializer.
/// </summary>
internal sealed class InspectWorktreeAgentResponse
{
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    /// <summary>True when the worktree is locked (<c>git worktree lock</c>). False (default) on an older Worker that does not report this (D5).</summary>
    [JsonPropertyName("isLocked")]
    public bool IsLocked { get; set; }

    /// <summary>Optional lock reason when <see cref="IsLocked"/> is true.</summary>
    [JsonPropertyName("lockReason")]
    public string? LockReason { get; set; }

    [JsonPropertyName("isDirty")]
    public bool? IsDirty { get; set; }

    /// <summary>True when the current branch has a configured upstream. Null means unknown (older Worker, or not reported).</summary>
    [JsonPropertyName("hasUpstream")]
    public bool? HasUpstream { get; set; }

    /// <summary>Commits on HEAD not on the upstream. Null when there is no upstream, or unknown (older Worker).</summary>
    [JsonPropertyName("aheadOfUpstream")]
    public int? AheadOfUpstream { get; set; }

    /// <summary>Commits on HEAD not on origin/defaultBranch. Null when that ref is missing, or unknown (older Worker).</summary>
    [JsonPropertyName("aheadOfDefault")]
    public int? AheadOfDefault { get; set; }

    /// <summary>Null on success; otherwise a short description of what could not be determined.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
