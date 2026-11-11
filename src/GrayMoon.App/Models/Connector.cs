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
    public ConnectorType ConnectorType { get; set; } = ConnectorType.GitHub;

    [Required]
    [MaxLength(300)]
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";

    [MaxLength(100)]
    public string? UserName { get; set; }

    // Made nullable - not required for NuGet.org
    // Token requirement determined by ConnectorType + ApiBaseUrl pattern
    [MaxLength(500)]
    public string? UserToken { get; set; }

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "Unknown";

    public bool IsActive { get; set; } = true;

    [MaxLength(1000)]
    public string? LastError { get; set; }

    /// <summary>Convenience flag indicating whether the connector is currently healthy (token present, decryptable, and validated).</summary>
    public bool IsHealthy { get; set; } = false;

    /// <summary>
    /// Set when the user removed the connector while a Workspace still used one of its repositories. The row then
    /// stays (without its token, inactive, under a changed name) only so those repositories keep a connector row;
    /// it is hidden from every connector list and purged once no repository is left on it
    /// (<c>RemovedConnectorCleanup</c>). Null for a normal connector.
    /// </summary>
    public DateTime? RemovedAtUtc { get; set; }

    /// <summary>True for a removed connector: its repositories have no valid connector until another one returns them.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool IsRemoved => RemovedAtUtc != null;

    /// <summary>Shown when work that needs a token is asked for a repository whose connector was removed.</summary>
    public static string NoValidConnectorMessage(string repositoryName) =>
        $"Repository {repositoryName} has no valid connector. Add or repair a connector that can access it, then fetch repositories.";
}
