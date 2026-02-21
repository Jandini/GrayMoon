namespace GrayMoon.App.Models;

/// <summary>Payload for dependency-synchronized push: one repo and the package dependencies (from lower-level repos) that must be in the registry before pushing.</summary>
public sealed record PushRepoPayload(
    int RepoId,
    string RepoName,
    int? DependencyLevel,
    IReadOnlyList<RequiredPackageForPush> RequiredPackages);

/// <summary>A package (id + version) that must be present in its matched registry before a dependent repo can be pushed.</summary>
public sealed record RequiredPackageForPush(string PackageId, string Version, int? MatchedConnectorId);
