namespace GrayMoon.App.Models;

public class RepoGitVersionInfo
{
    public string Version { get; init; } = "-";
    public string Branch { get; init; } = "-";
    public int? Projects { get; init; }
}
