using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

[Table("WorkspaceRepositories")]
public class WorkspaceRepositoryLink
{
    public int WorkspaceRepositoryLinkId { get; set; }

    [Required]
    public int WorkspaceId { get; set; }

    [ForeignKey(nameof(WorkspaceId))]
    public Workspace? Workspace { get; set; }

    [Required]
    public int GitHubRepositoryId { get; set; }

    [ForeignKey(nameof(GitHubRepositoryId))]
    public GitHubRepository? GitHubRepository { get; set; }

    [MaxLength(100)]
    public string? GitVersion { get; set; }

    [MaxLength(200)]
    public string? BranchName { get; set; }
}
