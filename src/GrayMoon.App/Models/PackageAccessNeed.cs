namespace GrayMoon.App.Models;

/// <summary>
/// A workspace package that projects in a repository restore. <see cref="ViaPackageId"/> is null when a project in the
/// repository references it directly, otherwise it names the workspace package that brings this one in transitively
/// (restore still downloads it, so the repository needs read access to it).
/// </summary>
public sealed record PackageAccessNeed(string PackageId, string? ViaPackageId)
{
    public bool IsDirect => ViaPackageId == null;
}
