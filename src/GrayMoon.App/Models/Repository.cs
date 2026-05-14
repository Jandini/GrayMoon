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

    /// <summary>Numeric repository ID from the provider (e.g. GitHub repo.id). 0 for legacy rows created before this column existed.</summary>
    public long GitHubRepositoryId { get; set; }

    /// <summary>Global node ID from the provider (e.g. GitHub node_id). Used as a secondary stable identity.</summary>
    [MaxLength(100)]
    public string? NodeId { get; set; }

    /// <summary>Comma-separated repository topics from GitHub.</summary>
    [MaxLength(2000)]
    public string? Topics { get; set; }
}
