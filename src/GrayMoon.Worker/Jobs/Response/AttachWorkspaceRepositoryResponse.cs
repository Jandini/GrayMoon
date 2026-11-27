using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class AttachWorkspaceRepositoryResponse : ICommandOutcome
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    /// <summary>True when the checked-out branch has no commit yet (the remote was empty).</summary>
    [JsonPropertyName("isUnborn")]
    public bool IsUnborn { get; set; }

    /// <summary>True when the folder was already this repository, so its files were left unchanged.</summary>
    [JsonPropertyName("adoptedExisting")]
    public bool AdoptedExisting { get; set; }

    /// <summary>
    /// Files that were already in the folder and differ from the repository's version. They were kept and show as
    /// modified. At most <see cref="MaxKeptFiles"/> names; <see cref="KeptFileCount"/> is the full count.
    /// </summary>
    [JsonPropertyName("keptFiles")]
    public List<string>? KeptFiles { get; set; }

    [JsonPropertyName("keptFileCount")]
    public int KeptFileCount { get; set; }

    public const int MaxKeptFiles = 20;
}
