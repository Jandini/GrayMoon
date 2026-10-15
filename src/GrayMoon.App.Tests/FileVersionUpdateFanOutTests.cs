using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// <see cref="WorkspaceFileVersionService.UpdateAllVersionsAsync(int, IReadOnlySet{int}?, bool, Action{string}?, CancellationToken)"/>
/// sends one UpdateFileVersions command per configured file: distinct files fan out up to
/// <see cref="WorkspaceOptions.MaxParallelOperations"/>, configs on the same file stay sequential, and a failing file
/// never affects the others.
/// </summary>
public sealed class FileVersionUpdateFanOutTests
{
    private const int FileCount = 12;
    private static readonly TimeSpan CommandDelay = TimeSpan.FromMilliseconds(60);

    [Fact]
    public async Task Distinct_files_fan_out_up_to_the_limit_and_never_beyond()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync(); // MaxParallelOperations = 4
        await SeedFilesAsync(ctx, FileCount);
        var probe = new FileVersionFanOutProbe();
        ctx.WorkerBridge.RespondAsync("UpdateFileVersions", async (args, ct) =>
        {
            await probe.RunAsync(FilePathOf(args), CommandDelay, ct);
            return Updated(2);
        });

        var result = await UpdateAsync(ctx);

        Assert.Null(result.Error);
        Assert.Equal(FileCount * 2, result.Updated);
        Assert.Equal(0, result.Failed);
        Assert.Equal(FileCount, probe.Started);
        Assert.Equal(4, probe.MaxActive);
    }

    [Fact]
    public async Task Limit_of_one_keeps_updates_sequential()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync(
            configureServices: s => s.Configure<WorkspaceOptions>(o => o.MaxParallelOperations = 1));
        await SeedFilesAsync(ctx, 5);
        var probe = new FileVersionFanOutProbe();
        ctx.WorkerBridge.RespondAsync("UpdateFileVersions", async (args, ct) =>
        {
            await probe.RunAsync(FilePathOf(args), TimeSpan.FromMilliseconds(20), ct);
            return Updated(1);
        });

        var result = await UpdateAsync(ctx);

        Assert.Equal(5, result.Updated);
        Assert.Equal(1, probe.MaxActive);
    }

    [Fact]
    public async Task Counters_callbacks_and_updated_files_are_correct_and_one_failure_does_not_affect_the_rest()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedFilesAsync(ctx, FileCount);
        var probe = new FileVersionFanOutProbe();
        ctx.WorkerBridge.RespondAsync("UpdateFileVersions", async (args, ct) =>
        {
            var path = FilePathOf(args);
            await probe.RunAsync(path, TimeSpan.FromMilliseconds(20), ct);
            return path switch
            {
                "docs/file-03.txt" => new WorkerCommandResponse(false, null, "boom"),
                "docs/file-05.txt" => throw new InvalidOperationException("worker exploded"),
                "docs/file-07.txt" => Updated(0),
                _ => Updated(3),
            };
        });
        var callbacks = new List<string>();

        var result = await UpdateAsync(ctx, onFileUpdated: callbacks.Add);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Failed);
        Assert.Equal((FileCount - 3) * 3, result.Updated);
        var expectedPaths = Enumerable.Range(0, FileCount)
            .Select(Path)
            .Where(p => p is not ("docs/file-03.txt" or "docs/file-05.txt" or "docs/file-07.txt"))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expectedPaths, result.UpdatedFiles.Select(f => f.FilePath).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(expectedPaths, callbacks.Order(StringComparer.Ordinal).ToList());
        Assert.All(result.UpdatedFiles, f => Assert.Equal("graymoon-api", f.RepoName));
        Assert.Equal(FileCount, probe.Started);
    }

    [Fact]
    public async Task Groups_run_concurrently_but_items_of_one_group_never_overlap()
    {
        var probe = new FileVersionFanOutProbe();
        IReadOnlyList<IReadOnlyList<string>> groups =
        [
            ["a/1", "a/2", "a/3"],
            ["b/1", "b/2"],
            ["c/1", "c/2", "c/3", "c/4"],
            ["d/1"],
            ["e/1", "e/2"],
        ];
        var processed = new System.Collections.Concurrent.ConcurrentBag<string>();

        await WorkspaceFileVersionService.RunGroupsBoundedAsync(groups, 3, async (item, ct) =>
        {
            await probe.RunAsync(item[..1], TimeSpan.FromMilliseconds(30), ct); // key = group letter
            processed.Add(item);
        }, CancellationToken.None);

        Assert.Equal(groups.Sum(g => g.Count), processed.Count);
        Assert.Equal(processed.Count, processed.Distinct().Count());
        Assert.Equal(1, probe.MaxActivePerKey);
        Assert.Equal(3, probe.MaxActive);
    }

    [Fact]
    public async Task Cancellation_propagates_out_of_the_fan_out()
    {
        using var cts = new CancellationTokenSource();
        IReadOnlyList<IReadOnlyList<int>> groups = [[1], [2], [3]];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WorkspaceFileVersionService.RunGroupsBoundedAsync(groups, 2, async (_, ct) =>
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
            }, cts.Token));
    }

    private static string Path(int i) => $"docs/file-{i:D2}.txt";

    private static string FilePathOf(object args) =>
        (string)args.GetType().GetProperty("filePath")!.GetValue(args)!;

    private static WorkerCommandResponse Updated(int count) => new(true, new { updatedCount = count }, null);

    private static async Task<(int Updated, int Failed, string? Error, IReadOnlyList<(int RepositoryId, string RepoName, string FilePath)> UpdatedFiles)> UpdateAsync(
        SyncStateTestContext ctx, Action<string>? onFileUpdated = null)
    {
        await using var scope = ctx.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>();
        return await service.UpdateAllVersionsAsync(ctx.WorkspaceId, onFileUpdated: onFileUpdated);
    }

    private static async Task SeedFilesAsync(SyncStateTestContext ctx, int count)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var i = 0; i < count; i++)
        {
            var file = new WorkspaceFile
            {
                WorkspaceId = ctx.WorkspaceId,
                RepositoryId = ctx.RepositoryId,
                FileName = $"file-{i:D2}.txt",
                FilePath = Path(i),
            };
            db.WorkspaceFiles.Add(file);
            await db.SaveChangesAsync();
            db.WorkspaceFileVersionConfigs.Add(new WorkspaceFileVersionConfig
            {
                FileId = file.FileId,
                VersionPattern = "V={graymoon-api}",
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Tracks how many calls overlap, overall and per key.</summary>
    private sealed class FileVersionFanOutProbe
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _activePerKey = new(StringComparer.Ordinal);
        private int _active;

        public int Started { get; private set; }
        public int MaxActive { get; private set; }
        public int MaxActivePerKey { get; private set; }

        public async Task RunAsync(string key, TimeSpan duration, CancellationToken ct)
        {
            lock (_gate)
            {
                Started++;
                _active++;
                MaxActive = Math.Max(MaxActive, _active);
                _activePerKey[key] = _activePerKey.GetValueOrDefault(key) + 1;
                MaxActivePerKey = Math.Max(MaxActivePerKey, _activePerKey[key]);
            }

            try
            {
                await Task.Delay(duration, ct);
            }
            finally
            {
                lock (_gate)
                {
                    _active--;
                    _activePerKey[key]--;
                }
            }
        }
    }
}
