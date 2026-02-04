using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

public class GitHubRepository
{
    public int GitHubRepositoryId { get; set; }

    [Required]
    public int GitHubConnectorId { get; set; }

    [ForeignKey(nameof(GitHubConnectorId))]
    public GitHubConnector? GitHubConnector { get; set; }

    [Required]
    [MaxLength(200)]
    public string RepositoryName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? OrgName { get; set; }

    [Required]
    [MaxLength(20)]
    public string Visibility { get; set; } = "Public";

    [Required]
    [MaxLength(500)]
    public string CloneUrl { get; set; } = string.Empty;

    /// <summary>Number of .csproj projects in the repository. Updated during workspace sync.</summary>
    public int? ProjectCount { get; set; }
}
