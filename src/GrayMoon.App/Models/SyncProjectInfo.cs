namespace GrayMoon.App.Models;

/// <summary>Project info from sync response (parsed from agent CsProjFileInfo).</summary>
public sealed record SyncProjectInfo(
    string ProjectName,
    ProjectType ProjectType,
    string ProjectFilePath,
    string TargetFramework,
    string? PackageId);
