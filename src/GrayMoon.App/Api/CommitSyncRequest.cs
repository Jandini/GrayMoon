namespace GrayMoon.App.Api;

public sealed class CommitSyncRequest
{
    public int RepositoryId { get; set; }
    public int WorkspaceId { get; set; }
}
