using GrayMoon.Agent.Abstractions;

namespace GrayMoon.Agent.Services;

public sealed class CsProjFileService : ICsProjFileService
{
    private const int MaxConcurrentSubdirSearches = 8;

    public async Task<int> FindAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return 0;

        try
        {
            var rootCount = CountCsprojInDirectory(repoPath, topLevelOnly: true);

            var subdirs = Directory.GetDirectories(repoPath)
                .Where(d => !string.Equals(Path.GetFileName(d), ".git", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (subdirs.Count == 0)
                return rootCount;

            using var semaphore = new SemaphoreSlim(MaxConcurrentSubdirSearches);
            var subdirCounts = await Task.WhenAll(subdirs.Select(async subdir =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return CountCsprojInDirectory(subdir, topLevelOnly: false);
                }
                finally
                {
                    semaphore.Release();
                }
            }));

            return rootCount + subdirCounts.Sum();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountCsprojInDirectory(string path, bool topLevelOnly)
    {
        try
        {
            var option = topLevelOnly ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
            return Directory.EnumerateFiles(path, "*.csproj", option).Count();
        }
        catch
        {
            return 0;
        }
    }
}
