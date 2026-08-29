namespace GrayMoon.App.Models;

/// <summary>
/// One resolved virtual/generated NuGet package dependency: a consumer repository's real .csproj (identified by
/// <see cref="ConsumerProjectFilePath"/>) references a package produced by <see cref="ProducerRepositoryId"/> with
/// no physical package-producing .csproj, inferred from a configured .csproj version file's version pattern.
/// </summary>
public sealed record GeneratedPackageDependencyInfo(
    int ConsumerRepositoryId,
    string ConsumerProjectFilePath,
    int ProducerRepositoryId,
    string PackageName);
