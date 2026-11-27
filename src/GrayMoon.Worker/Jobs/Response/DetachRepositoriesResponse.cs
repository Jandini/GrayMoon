using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Response;

public sealed class DetachRepositoriesResponse : ICommandOutcome
{
    /// <summary>True only when no repository failed.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>The failures are per repository, in <see cref="Results"/>; there is no message for the whole command.</summary>
    string? ICommandOutcome.ErrorMessage => null;

    [JsonPropertyName("results")]
    public List<DetachRepositoriesResult> Results { get; set; } = [];

    /// <summary>Folders the Worker was watching and has let go of.</summary>
    [JsonPropertyName("releasedWatchers")]
    public List<string> ReleasedWatchers { get; set; } = [];
}

public sealed class DetachRepositoriesResult
{
    [JsonPropertyName("repositoryId")]
    public int RepositoryId { get; set; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; set; }

    /// <summary>The repository's working tree; null when the name could not be resolved to a folder.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>Removed, NothingToRemove, NotFound, NotARepository or Failed.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "";

    /// <summary>GitFailed, HookNotDeleted, HookNotRestored, WorkerBusy or InvalidName; null unless the outcome is Failed.</summary>
    [JsonPropertyName("failureKind")]
    public string? FailureKind { get; set; }

    [JsonPropertyName("failedPath")]
    public string? FailedPath { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("removedHooks")]
    public List<string> RemovedHooks { get; set; } = [];

    [JsonPropertyName("restoredHooks")]
    public List<string> RestoredHooks { get; set; } = [];

    [JsonPropertyName("foreignHooks")]
    public List<string> ForeignHooks { get; set; } = [];

    [JsonPropertyName("leftoverReplacedHooks")]
    public List<string> LeftoverReplacedHooks { get; set; } = [];
}
