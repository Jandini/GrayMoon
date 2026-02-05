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
}
