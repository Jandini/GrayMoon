using System.Diagnostics;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services;

public sealed class CsProjFileService(ICsProjFileParser parser, IGitIgnoreService ignore, ILogger<CsProjFileService> logger) : ICsProjFileService
{
    private const int DefaultMaxParallel = 8;

    public async Task<IReadOnlyList<CsProjFileInfo>> FindAsync(string repoPath, CancellationToken cancellationToken = default, int? maxParallel = null)
    {
        var paths = await GetProjectPathsAsync(repoPath, cancellationToken, maxParallel);
        if (paths.Count == 0)
            return [];

        var limit = Math.Max(1, maxParallel ?? DefaultMaxParallel);
        var results = new List<CsProjFileInfo>();
        using var semaphore = new SemaphoreSlim(limit);
        var tasks = paths.Select(async path =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var parsed = await parser.ParseAsync(path, cancellationToken);
                    if (parsed != null)
                        return new CsProjFileInfo
                        {
                            ProjectPath = Path.GetRelativePath(repoPath, path),
                            ProjectType = parsed.ProjectType,
                            TargetFramework = parsed.TargetFramework,
                            Name = parsed.Name,
                            PackageId = parsed.PackageId,
                            PackageReferences = parsed.PackageReferences
                        };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A file that does not parse is skipped; it does not affect others.
                    logger.LogDebug(ex, "Skipping unparsable project {ProjectPath}", path);
                }
                return null;
            }
            finally
            {
                semaphore.Release();
            }
        });
        var parsedResults = await Task.WhenAll(tasks);
        foreach (var r in parsedResults)
        {
            if (r != null)
                results.Add(r);
        }
        return results;
    }

    public Task<IReadOnlyList<string>> GetProjectPathsAsync(string repoPath, CancellationToken cancellationToken = default, int? maxParallel = null)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return Task.FromResult<IReadOnlyList<string>>([]);

        // One synchronous walk on one Repository (not thread-safe). Parsing stays parallel in FindAsync.
        var sw = Stopwatch.StartNew();
        var stats = new WalkStats();
        var results = new List<string>();
        try
        {
            using var session = ignore.Open(repoPath);
            Walk(session, repoPath, repoPath, relativeDir: "", results, stats, cancellationToken);
        }
        catch (GitIgnoreException ex)
        {
            throw new ProjectDiscoveryException(repoPath, ex.Message, ex);
        }

        results.Sort(StringComparer.Ordinal);
        logger.LogDebug(
            "Git ignore/project discovery: repo={RepoPath} elapsedMs={ElapsedMs} directoriesVisited={Visited} directoriesPruned={Pruned} nestedReposSkipped={Nested} candidateFiles={Candidates} excludedCandidates={ExcludedCandidates} returnedFiles={Returned}",
            repoPath, sw.ElapsedMilliseconds, stats.Visited, stats.Pruned, stats.NestedRepos, stats.Candidates, stats.ExcludedCandidates, results.Count);
        return Task.FromResult<IReadOnlyList<string>>(results);
    }

    private sealed class WalkStats
    {
        public int Visited, Pruned, NestedRepos, Candidates, ExcludedCandidates;
    }

    private static void Walk(
        IGitIgnoreSession session, string repoRoot, string directory, string relativeDir,
        List<string> results, WalkStats stats, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stats.Visited++;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.csproj"))
            {
                stats.Candidates++;
                var rel = relativeDir.Length == 0 ? Path.GetFileName(file) : relativeDir + "/" + Path.GetFileName(file);
                if (session.IsExcluded(rel, GitPathKind.File))
                    stats.ExcludedCandidates++;
                else
                    results.Add(file);
            }

            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(sub);
                if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (WorkerRepositoryPaths.HasGitMetadata(sub))
                {
                    stats.NestedRepos++; // Git never tracks the contents of a nested repository or submodule.
                    continue;
                }

                var rel = relativeDir.Length == 0 ? name : relativeDir + "/" + name;
                if (session.IsExcluded(rel, GitPathKind.Directory))
                {
                    stats.Pruned++;
                    continue;
                }

                Walk(session, repoRoot, sub, rel, results, stats, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectDiscoveryException(repoRoot, $"Cannot read directory '{directory}': {ex.Message}", ex);
        }
    }

    public Task<CsProjFileInfo?> ParseAsync(string csprojPath, CancellationToken cancellationToken = default) =>
        parser.ParseAsync(csprojPath, cancellationToken);

    public async Task<int> UpdatePackageVersionsAsync(string repoPath, IReadOnlyList<(string ProjectPath, IReadOnlyDictionary<string, string> PackageUpdates)> projectUpdates, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath) || projectUpdates == null || projectUpdates.Count == 0)
            return 0;

        var updatedCount = 0;
        foreach (var (relativePath, packageUpdates) in projectUpdates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(relativePath) || packageUpdates == null || packageUpdates.Count == 0)
                continue;

            var fullPath = Path.Combine(repoPath, relativePath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!File.Exists(fullPath))
                continue;

            var modified = await parser.UpdateAsync(fullPath, packageUpdates, cancellationToken);
            if (modified)
                updatedCount++;
        }

        return updatedCount;
    }

}
