using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Services;

public sealed class CsProjFileService(ICsProjFileParser parser) : ICsProjFileService
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
                catch
                {
                    // Skip this file; do not affect others
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

    public async Task<IReadOnlyList<string>> GetProjectPathsAsync(string repoPath, CancellationToken cancellationToken = default, int? maxParallel = null)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return [];

        var limit = Math.Max(1, maxParallel ?? DefaultMaxParallel);
        try
        {
            var rootPaths = EnumerateCsprojInDirectory(repoPath, topLevelOnly: true);

            var subdirs = Directory.GetDirectories(repoPath)
                .Where(d => !string.Equals(Path.GetFileName(d), ".git", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (subdirs.Count == 0)
                return rootPaths;

            using var semaphore = new SemaphoreSlim(limit);
            var subdirPaths = await Task.WhenAll(subdirs.Select(async subdir =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return EnumerateCsprojInDirectory(subdir, topLevelOnly: false);
                }
                finally
                {
                    semaphore.Release();
                }
            }));

            return rootPaths.Concat(subdirPaths.SelectMany(x => x)).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
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

    private static List<string> EnumerateCsprojInDirectory(string path, bool topLevelOnly)
    {
        try
        {
            var option = topLevelOnly ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
            return Directory.EnumerateFiles(path, "*.csproj", option).ToList();
        }
        catch
        {
            return [];
        }
    }
}
