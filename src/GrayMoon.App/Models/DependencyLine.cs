namespace GrayMoon.App.Models;

/// <summary>Describes a single package dependency of a workspace repository (package ID and its current version).</summary>
public sealed record DependencyLine(string PackageId, string Version);
