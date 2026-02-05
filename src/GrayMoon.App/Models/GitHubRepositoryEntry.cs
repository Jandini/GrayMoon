namespace GrayMoon.App.Models;

public class GitHubRepositoryEntry
{
    public int RepositoryId { get; set; }
    public string ConnectorName { get; set; } = string.Empty;
    public string? OrgName { get; set; }
    public string RepositoryName { get; set; } = string.Empty;
    public string Visibility { get; set; } = "Public";
    public string CloneUrl { get; set; } = string.Empty;
}
