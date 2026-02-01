using System.ComponentModel.DataAnnotations;

namespace GrayMoon.App.Models;

public class GitHubConnectorForm
{
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
}
