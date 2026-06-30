namespace GrayMoon.App.Models;

/// <summary>Describes a single package dependency that is out of date for a workspace repository.</summary>
public sealed record DependencyMismatchLine(string PackageId, string CurrentVersion, string NewVersion);
