namespace GrayMoon.App.Models;

public class GitHubOptions
{
    public string PersonalAccessToken { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";
}
