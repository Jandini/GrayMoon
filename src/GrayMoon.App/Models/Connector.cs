using System.ComponentModel.DataAnnotations;

namespace GrayMoon.App.Models;

/// <summary>Connector (formerly GitHubConnector). Table: Connectors.</summary>
public class Connector
{
    public int ConnectorId { get; set; }

    [Required]
    [MaxLength(100)]
    public string ConnectorName { get; set; } = "GitHub";

    [Required]
    [MaxLength(300)]
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";

    [MaxLength(100)]
    public string? UserName { get; set; }

    [Required]
    [MaxLength(500)]
    public string UserToken { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "Unknown";

    public bool IsActive { get; set; } = true;

    [MaxLength(1000)]
    public string? LastError { get; set; }
}
