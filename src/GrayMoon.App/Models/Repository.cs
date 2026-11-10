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

    public bool Archived { get; set; }

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

    /// <summary>True when the provider reports this repository as a fork of another repository.</summary>
    public bool IsFork { get; set; }

    /// <summary>Clone URL of the repository this one was forked from (the immediate parent). Null when not a fork or when the parent could not be resolved yet.</summary>
    [MaxLength(500)]
    public string? ParentCloneUrl { get; set; }

    /// <summary>
    /// When a refresh first found that the connector no longer returns this repository while a Workspace still
    /// links it. Such a repository is kept, never deleted; null when the provider returns it (cleared by the next
    /// fetch that does).
    /// </summary>
    public DateTime? MissingFromProviderSinceUtc { get; set; }
}
