namespace GrayMoon.App.Models;

/// <summary>Item to add to workspace files (repository-scoped file).</summary>
public sealed class WorkspaceFileAddItem
{
    public int RepositoryId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
}
