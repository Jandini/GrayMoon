using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class RemoveGitWorktreeResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>True when the path was already absent from Git's worktree list.</summary>
    [JsonPropertyName("alreadyRemoved")]
    public bool AlreadyRemoved { get; set; }

    /// <summary>True when files remain on disk after this call, whether deletion failed or a safety guard blocked it.</summary>
    [JsonPropertyName("residueRemaining")]
    public bool ResidueRemaining { get; set; }

    /// <summary>Count of files left behind; 0 when nothing is left.</summary>
    [JsonPropertyName("residueFileCount")]
    public int ResidueFileCount { get; set; }

    /// <summary>Up to 5 relative paths of files left behind, for the Remove report.</summary>
    [JsonPropertyName("residueSampleFiles")]
    public List<string>? ResidueSampleFiles { get; set; }

    /// <summary>Null when there is no residue; otherwise why cleanup did not finish (a safety guard, a locked file, or similar).</summary>
    [JsonPropertyName("residueMessage")]
    public string? ResidueMessage { get; set; }
}
