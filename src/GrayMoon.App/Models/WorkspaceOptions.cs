namespace GrayMoon.App.Models;

public class WorkspaceOptions
{
    public string RootPath { get; set; } = @"C:\Projectes";

    public int MaxConcurrentGitOperations { get; set; } = 8;
}
