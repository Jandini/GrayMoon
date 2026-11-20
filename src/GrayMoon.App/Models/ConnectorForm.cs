using System.ComponentModel.DataAnnotations;

namespace GrayMoon.App.Models;

public class ConnectorForm
{
    [Required]
    public ConnectorType ConnectorType { get; set; } = ConnectorType.GitHub;

    [Required]
    [MaxLength(100)]
    public string ConnectorName { get; set; } = "GitHub";

    [Required]
    [MaxLength(300)]
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";

    [MaxLength(100)]
    public string? UserName { get; set; }

    [MaxLength(500)]
    public string? UserToken { get; set; }

    /// <summary>
    /// ProGet only: the feed name, shown in its own box while the dialog is open. It is not stored on its own;
    /// the connector is saved with the feed endpoint (<c>.../nuget/FEED/v3/index.json</c>) as its API Base URL.
    /// </summary>
    [MaxLength(100)]
    public string? FeedName { get; set; }
}
