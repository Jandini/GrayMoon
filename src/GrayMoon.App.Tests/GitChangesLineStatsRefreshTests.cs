using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

public sealed class GitChangesLineStatsRefreshTests
{
    [Fact]
    public void RequestWorkspace_without_subscribe_does_not_scan()
    {
        var (refresh, scanner) = CreateRefresh();

        refresh.RequestWorkspace(8);

        Assert.Equal(0, scanner.Calls);
    }

    [Fact]
    public void RequestRepository_without_subscribe_does_not_scan()
    {
        var (refresh, scanner) = CreateRefresh();

        refresh.RequestRepository(8, 3);

        Assert.Equal(0, scanner.Calls);
    }

    [Fact]
    public async Task RequestWorkspace_while_subscribed_scans_with_line_stats()
    {
        var (refresh, scanner) = CreateRefresh();
        using var lease = refresh.Subscribe(8);

        refresh.RequestWorkspace(8);

        await WaitUntilAsync(() => scanner.Calls == 1);
        Assert.Equal(8, scanner.LastWorkspaceId);
        Assert.True(scanner.LastIncludeLineStats);
        Assert.Null(scanner.LastRepositoryId);
    }

    [Fact]
    public async Task RequestWorkspace_coalesces_while_in_flight()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (refresh, scanner) = CreateRefresh(async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        using var lease = refresh.Subscribe(8);

        refresh.RequestWorkspace(8);
        await entered.Task;
        refresh.RequestWorkspace(8);
        refresh.RequestRepository(8, 3);

        Assert.Equal(1, scanner.Calls);

        release.SetResult();
        await WaitUntilAsync(() => scanner.Completed == 1);
    }

    [Fact]
    public async Task RequestRepository_while_subscribed_scans_that_repository()
    {
        var (refresh, scanner) = CreateRefresh();
        using var lease = refresh.Subscribe(8);

        refresh.RequestRepository(8, 3);

        await WaitUntilAsync(() => scanner.Calls == 1);
        Assert.Equal(8, scanner.LastWorkspaceId);
        Assert.Equal(3, scanner.LastRepositoryId);
        Assert.True(scanner.LastIncludeLineStats);
    }

    [Fact]
    public async Task RequestRepository_debounces_a_burst_into_one_scan()
    {
        var (refresh, scanner) = CreateRefresh();
        using var lease = refresh.Subscribe(8);

        refresh.RequestRepository(8, 3);
        refresh.RequestRepository(8, 3);
        refresh.RequestRepository(8, 3);

        await WaitUntilAsync(() => scanner.Calls == 1);
        await Task.Delay(30);
        Assert.Equal(1, scanner.Calls);
        Assert.Equal(3, scanner.LastRepositoryId);
    }

    [Fact]
    public async Task Dispose_unsubscribes_and_cancels_pending_repository_fill()
    {
        var (refresh, scanner) = CreateRefresh();
        var lease = refresh.Subscribe(8);

        refresh.RequestRepository(8, 3);
        lease.Dispose();

        await Task.Delay(30);
        Assert.Equal(0, scanner.Calls);
    }

    private static (GitChangesLineStatsRefresh Refresh, RecordingScanner Scanner) CreateRefresh(
        Func<Task>? onScan = null)
    {
        var scanner = new RecordingScanner(onScan);
        var refresh = new GitChangesLineStatsRefresh(
            scanner,
            Options.Create(new GitChangesOptions { WatcherDebounceMilliseconds = 10 }),
            NullLogger<GitChangesLineStatsRefresh>.Instance);
        return (refresh, scanner);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for background +/- fill.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class RecordingScanner(Func<Task>? onScan) : IGitChangesWorkspaceScanner
    {
        public int Calls;
        public int Completed;
        public int? LastWorkspaceId;
        public int? LastRepositoryId;
        public bool LastIncludeLineStats;

        public async Task ScanWorkspaceAsync(
            int workspaceId,
            CancellationToken cancellationToken,
            Action<GitChangesWorkspaceScanProgress>? onProgress = null,
            bool includeLineStats = false,
            int? repositoryId = null)
        {
            LastWorkspaceId = workspaceId;
            LastRepositoryId = repositoryId;
            LastIncludeLineStats = includeLineStats;
            Interlocked.Increment(ref Calls);
            if (onScan != null)
            {
                await onScan();
            }

            Interlocked.Increment(ref Completed);
        }
    }
}

internal sealed class NoopGitChangesLineStatsRefresh : IGitChangesLineStatsRefresh
{
    public IDisposable Subscribe(int workspaceId) => new NoopLease();

    public void RequestWorkspace(int workspaceId)
    {
    }

    public void RequestRepository(int workspaceId, int repositoryId)
    {
    }

    private sealed class NoopLease : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
