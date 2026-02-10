using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

/// <summary>Repository (formerly GitHubRepository). Table: Repositories.</summary>
public class Repository
{
    public int RepositoryId { get; set; }

    [Required]
    public int ConnectorId { get; set; }

    [ForeignKey(nameof(ConnectorId))]
    public Connector? Connector { get; set; }

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
}
