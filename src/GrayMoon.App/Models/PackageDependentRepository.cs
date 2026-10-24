namespace GrayMoon.App.Models;

/// <summary>
/// A workspace repository whose projects restore a workspace package. <see cref="ViaPackageId"/> is null when a project
/// references the package directly, otherwise it names the workspace package the repository references that brings
/// this one in transitively (restore still downloads it, so the repository needs read access to it).
/// </summary>
public sealed record PackageDependentRepository(int RepositoryId, string RepositoryName, string? ViaPackageId)
{
    public bool IsDirect => ViaPackageId == null;
}
