namespace GrayMoon.App.Models;

/// <summary>Package reference from .csproj (Include and Version).</summary>
public sealed record SyncPackageReference(string Name, string Version);

/// <summary>Project info from sync response (parsed from agent CsProjFileInfo).</summary>
public sealed record SyncProjectInfo(
    string ProjectName,
    ProjectType ProjectType,
    string ProjectFilePath,
    string TargetFramework,
    string? PackageId,
    IReadOnlyList<SyncPackageReference> PackageReferences);
