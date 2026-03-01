namespace GrayMoon.App.Models;

/// <summary>Payload for syncing dependency versions in one repository.</summary>
public sealed record SyncDependenciesRepoPayload(
    int RepoId,
    string RepoName,
    int? DependencyLevel,
    IReadOnlyList<SyncDependenciesProjectUpdate> ProjectUpdates);

/// <summary>Updates to apply to one .csproj file.</summary>
public sealed record SyncDependenciesProjectUpdate(
    string ProjectPath,
    IReadOnlyList<(string PackageId, string CurrentVersion, string NewVersion)> PackageUpdates);
