namespace GrayMoon.Agent.Abstractions;

/// <summary>Finds .csproj files within a repository path (root and subdirectories, excluding .git), with parallel subdirectory search.</summary>
public interface ICsProjFileService
{
    /// <summary>Finds all *.csproj in repo root and in each direct subdirectory (except .git), searching subdirs in parallel (up to 8 at a time). Returns the total count.</summary>
    Task<int> FindAsync(string repoPath, CancellationToken cancellationToken = default);
}
