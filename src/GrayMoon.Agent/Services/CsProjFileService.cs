using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Services;

public sealed class CsProjFileService(ICsProjFileParser parser) : ICsProjFileService
{
    private const int MaxConcurrentSubdirSearches = 8;
    private const int MaxConcurrentParses = 8;

    public async Task<IReadOnlyList<CsProjFileInfo>> FindAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var paths = await GetProjectPathsAsync(repoPath, cancellationToken);
        if (paths.Count == 0)
            return [];

        var results = new List<CsProjFileInfo>();
        using var semaphore = new SemaphoreSlim(MaxConcurrentParses);
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

    public async Task<IReadOnlyList<string>> GetProjectPathsAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return [];

        try
        {
            var rootPaths = EnumerateCsprojInDirectory(repoPath, topLevelOnly: true);

            var subdirs = Directory.GetDirectories(repoPath)
                .Where(d => !string.Equals(Path.GetFileName(d), ".git", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (subdirs.Count == 0)
                return rootPaths;

            using var semaphore = new SemaphoreSlim(MaxConcurrentSubdirSearches);
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
