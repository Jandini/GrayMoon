using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Abstractions;

/// <summary>Finds and parses .csproj files within a repository path (root and subdirectories) that Git does not exclude, with parallel parsing.</summary>
public interface ICsProjFileService
{
    /// <summary>Finds all *.csproj in repo root and subdirectories that Git does not exclude (see IGitIgnoreSession.IsExcluded; nested repositories are skipped), parses each in parallel (up to maxParallel at a time when specified), and returns parsed info for every successfully parsed file. A file that fails to parse is skipped. Throws when discovery itself fails.</summary>
    /// <param name="maxParallel">When null, uses a default (e.g. 8).</param>
    Task<IReadOnlyList<CsProjFileInfo>> FindAsync(string repoPath, CancellationToken cancellationToken = default, int? maxParallel = null);

    /// <summary>Returns full paths of all *.csproj in repo root and subdirectories that Git does not exclude, in ordinal order. Throws (never returns a partial list) when the repository cannot be opened or a directory cannot be read.</summary>
    /// <param name="maxParallel">When null, uses a default (e.g. 8).</param>
    Task<IReadOnlyList<string>> GetProjectPathsAsync(string repoPath, CancellationToken cancellationToken = default, int? maxParallel = null);

    /// <summary>Parses an SDK-style .csproj file and returns project type, target framework, name, and package references; returns null if the file is missing or invalid.</summary>
    Task<CsProjFileInfo?> ParseAsync(string csprojPath, CancellationToken cancellationToken = default);

    /// <summary>Updates only the Version of PackageReference elements for the given package IDs in each project file. Does not change any other content in the .csproj files.</summary>
    /// <param name="repoPath">Repository root path.</param>
    /// <param name="projectUpdates">List of (project path relative to repo, package ID to new version).</param>
    /// <returns>Number of project files that were modified.</returns>
    Task<int> UpdatePackageVersionsAsync(string repoPath, IReadOnlyList<(string ProjectPath, IReadOnlyDictionary<string, string> PackageUpdates)> projectUpdates, CancellationToken cancellationToken = default);
}

/// <summary>Project discovery could not complete. Never means "no projects"; callers must treat the probe as failed.</summary>
public sealed class ProjectDiscoveryException(string repositoryPath, string message, Exception? innerException = null)
    : Exception($"{message} (repository: {repositoryPath})", innerException)
{
    public string RepositoryPath { get; } = repositoryPath;
}
