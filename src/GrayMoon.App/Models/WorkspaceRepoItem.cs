namespace GrayMoon.App.Models;

/// <summary>Repository item for dropdowns (e.g. in FileFoundModal).</summary>
public sealed class WorkspaceRepoItem
{
    public int RepositoryId { get; set; }
    public string RepositoryName { get; set; } = string.Empty;
}
