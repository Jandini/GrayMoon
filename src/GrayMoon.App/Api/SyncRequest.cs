namespace GrayMoon.App.Api;

public sealed class SyncRequest
{
    public int RepositoryId { get; set; }
    public int WorkspaceId { get; set; }

    /// <summary>Optional. Indicates what triggered the sync (e.g. "post-commit", "post-checkout", "manual") for logging.</summary>
    public string? Trigger { get; set; }
}
