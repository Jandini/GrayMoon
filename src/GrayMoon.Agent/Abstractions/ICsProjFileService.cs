using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Abstractions;

/// <summary>Finds and parses .csproj files within a repository path (root and subdirectories, excluding .git), with parallel subdirectory search.</summary>
public interface ICsProjFileService
{
    /// <summary>Finds all *.csproj in repo root and subdirectories (except .git), parses each in parallel (up to 8 at a time), and returns parsed info for every successfully parsed file. Failed parses are skipped.</summary>
    Task<IReadOnlyList<CsProjFileInfo>> FindAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>Returns full paths of all *.csproj in repo root and subdirectories (except .git), searching subdirs in parallel.</summary>
    Task<IReadOnlyList<string>> GetProjectPathsAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>Parses an SDK-style .csproj file and returns project type, target framework, name, and package references; returns null if the file is missing or invalid.</summary>
    Task<CsProjFileInfo?> ParseAsync(string csprojPath, CancellationToken cancellationToken = default);

    /// <summary>Updates only the Version of PackageReference elements for the given package IDs in each project file. Does not change any other content in the .csproj files.</summary>
    /// <param name="repoPath">Repository root path.</param>
    /// <param name="projectUpdates">List of (project path relative to repo, package ID to new version).</param>
    /// <returns>Number of project files that were modified.</returns>
    Task<int> UpdatePackageVersionsAsync(string repoPath, IReadOnlyList<(string ProjectPath, IReadOnlyDictionary<string, string> PackageUpdates)> projectUpdates, CancellationToken cancellationToken = default);
}
