namespace GrayMoon.App.Models;

public class GitHubActionEntry
{
    public long RunId { get; set; }
    public long WorkflowId { get; set; }
    public string ConnectorName { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public string Event { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Conclusion { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string HtmlUrl { get; set; } = string.Empty;
    public string? HeadBranch { get; set; }
}
