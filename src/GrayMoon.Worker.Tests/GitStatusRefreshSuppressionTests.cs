using System.Collections.Concurrent;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using GrayMoon.Worker.Services.GitChanges;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Sync suppression scope on <see cref="GitStatusRefreshCoordinator"/>: watcher-originated refreshes are held back
/// while a GrayMoon-managed Sync rewrites .git, then exactly one debounced refresh runs when the last scope closes.
/// Manual refreshes are never suppressed. Driven through <c>MarkDirty</c> (what the watcher calls) against a fake
/// status service, so no real FileSystemWatcher timing is involved.
/// </summary>
public sealed class GitStatusRefreshSuppressionTests
{
    private const int DebounceMs = 60;

    private static GitStatusRefreshCoordinator CreateCoordinator(FakeRepositoryGitChangesService fake) =>
        new(
            fake,
            new GitChangesSnapshotCache(),
            Options.Create(new GitChangesOptions { WatcherDebounceMilliseconds = DebounceMs }),
            NullLogger<GitStatusRefreshCoordinator>.Instance);

    private static FakeRepositoryGitChangesService NewFake(int delayMs = 5) =>
        new() { Delay = TimeSpan.FromMilliseconds(delayMs) };

    [Fact]
    public async Task Watcher_events_during_a_scope_start_no_scan_and_release_schedules_exactly_one()
    {
        var fake = NewFake();
        using var coordinator = CreateCoordinator(fake);
        const string repo = @"C:\repo-sync";

        var scope = coordinator.BeginExternalRepositoryMutation(repo);
        for (var i = 0; i < 25; i++)
        {
            coordinator.MarkDirty(repo);
            await Task.Delay(5);
        }

        await Task.Delay(DebounceMs * 4);
        Assert.Equal(0, fake.CallCount);

        scope.Dispose();

        Assert.True(await WaitForAsync(() => fake.CallCount >= 1));
        await Task.Delay(DebounceMs * 4);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task A_scope_with_no_watcher_events_schedules_no_refresh()
    {
        var fake = NewFake();
        using var coordinator = CreateCoordinator(fake);

        coordinator.BeginExternalRepositoryMutation(@"C:\repo-quiet").Dispose();

        await Task.Delay(DebounceMs * 4);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task A_debounce_armed_before_the_scope_opened_does_not_scan_during_it()
    {
        var fake = NewFake();
        using var coordinator = CreateCoordinator(fake);
        const string repo = @"C:\repo-armed";

        coordinator.MarkDirty(repo);
        var scope = coordinator.BeginExternalRepositoryMutation(repo);

        await Task.Delay(DebounceMs * 4);
        Assert.Equal(0, fake.CallCount);

        scope.Dispose();

        Assert.True(await WaitForAsync(() => fake.CallCount >= 1));
        await Task.Delay(DebounceMs * 4);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task Events_during_an_in_flight_scan_queue_no_follow_up_while_suppressed()
    {
        var fake = NewFake(delayMs: 250);
        using var coordinator = CreateCoordinator(fake);
        const string repo = @"C:\repo-inflight";

        var manual = coordinator.RefreshNowAsync(repo, CancellationToken.None);
        await WaitForAsync(() => fake.CallCount >= 1);

        using (coordinator.BeginExternalRepositoryMutation(repo))
        {
            coordinator.MarkDirty(repo);
            await manual;
            await Task.Delay(DebounceMs * 4);
            Assert.Equal(1, fake.CallCount);
        }

        Assert.True(await WaitForAsync(() => fake.CallCount >= 2));
        await Task.Delay(DebounceMs * 4);
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task A_manual_refresh_runs_immediately_while_a_scope_is_open()
    {
        var fake = NewFake();
        using var coordinator = CreateCoordinator(fake);
        const string repo = @"C:\repo-manual";

        using var scope = coordinator.BeginExternalRepositoryMutation(repo);
        coordinator.MarkDirty(repo);

        var result = await coordinator.RefreshNowAsync(repo, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task Repositories_are_suppressed_independently()
    {
        var fake = NewFake();
        using var coordinator = CreateCoordinator(fake);
        const string a = @"C:\repo-a";
        const string b = @"C:\repo-b";

        var scopeA = coordinator.BeginExternalRepositoryMutation(a);
        var scopeB = coordinator.BeginExternalRepositoryMutation(b);
        coordinator.MarkDirty(a);
        coordinator.MarkDirty(b);

        scopeA.Dispose();

        Assert.True(await WaitForAsync(() => fake.CallCount >= 1));
        await Task.Delay(DebounceMs * 4);
        Assert.Equal(1, fake.CallCount);
        Assert.Equal(1, fake.CallsFor(a));
        Assert.Equal(0, fake.CallsFor(b));

        scopeB.Dispose();

        Assert.True(await WaitForAsync(() => fake.CallsFor(b) >= 1));
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task Overlapping_scopes_on_one_repository_release_together()
    {
        var fake = NewFake();
        using var coordinator = CreateCoordinator(fake);
        const string repo = @"C:\repo-overlap";

        var first = coordinator.BeginExternalRepositoryMutation(repo);
        var second = coordinator.BeginExternalRepositoryMutation(repo);
        coordinator.MarkDirty(repo);

        first.Dispose();
        await Task.Delay(DebounceMs * 4);
        Assert.Equal(0, fake.CallCount);

        second.Dispose();

        Assert.True(await WaitForAsync(() => fake.CallCount >= 1));
        await Task.Delay(DebounceMs * 4);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task Disposing_a_scope_twice_does_not_release_another_scope()
    {
        var fake = NewFake();
        using var coordinator = CreateCoordinator(fake);
        const string repo = @"C:\repo-twice";

        var first = coordinator.BeginExternalRepositoryMutation(repo);
        var second = coordinator.BeginExternalRepositoryMutation(repo);
        coordinator.MarkDirty(repo);

        first.Dispose();
        first.Dispose();

        await Task.Delay(DebounceMs * 4);
        Assert.Equal(0, fake.CallCount);
        second.Dispose();
        Assert.True(await WaitForAsync(() => fake.CallCount >= 1));
    }

    [Fact]
    public async Task A_failed_operation_still_releases_and_reconciles()
    {
        var fake = NewFake();
        using var coordinator = CreateCoordinator(fake);
        const string repo = @"C:\repo-failed";

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var scope = coordinator.BeginExternalRepositoryMutation(repo);
            coordinator.MarkDirty(repo);
            await Task.Yield();
            throw new InvalidOperationException("sync failed");
        });

        Assert.True(await WaitForAsync(() => fake.CallCount >= 1));
        coordinator.MarkDirty(repo); // no longer suppressed
        Assert.True(await WaitForAsync(() => fake.CallCount >= 2));
    }

    [Fact]
    public async Task A_cancelled_operation_does_not_leave_the_repository_suppressed()
    {
        var fake = NewFake();
        using var coordinator = CreateCoordinator(fake);
        const string repo = @"C:\repo-cancelled";
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var scope = coordinator.BeginExternalRepositoryMutation(repo);
            coordinator.MarkDirty(repo);
            await Task.Delay(Timeout.Infinite, cts.Token);
        });

        Assert.True(await WaitForAsync(() => fake.CallCount >= 1));
        await Task.Delay(DebounceMs * 4);
        coordinator.MarkDirty(repo);
        Assert.True(await WaitForAsync(() => fake.CallCount >= 2));
    }

    [Fact]
    public async Task A_tracker_removed_while_a_scope_is_open_does_not_break_release()
    {
        var fake = NewFake();
        using var coordinator = CreateCoordinator(fake);
        const string repo = @"C:\repo-removed";

        var scope = coordinator.BeginExternalRepositoryMutation(repo);
        coordinator.MarkDirty(repo);
        coordinator.RemoveTracker(repo);

        scope.Dispose(); // must not throw

        await Task.Delay(DebounceMs * 4);
        coordinator.MarkDirty(repo); // fresh tracker, unsuppressed
        Assert.True(await WaitForAsync(() => fake.CallCount >= 1));
    }

    [Fact]
    public async Task Sync_releases_its_scope_when_the_fetch_fails()
    {
        var root = Directory.CreateTempSubdirectory("graymoon-sync-suppress-").FullName;
        try
        {
            var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
            var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
            var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
            var git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());
            var suppressor = new RecordingSuppressor();

            // A folder that exists but is not a repository: AddSafeDirectory passes, the fetch fails.
            var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(root, "ws");
            Directory.CreateDirectory(Path.Combine(workspacePath, "repo"));

            var response = await new SyncRepositoryCommand(
                git, reader, new LibGit2SharpLocalGitSnapshotReader(), new CountingCsProjFileService(), CapabilityTestDoubles.RealFactory(git),
                logger: null, refreshSuppressor: suppressor).ExecuteAsync(new SyncRepositoryRequest
                {
                    WorkspaceRoot = root,
                    WorkspaceName = "ws",
                    RepositoryName = "repo",
                    RepositoryId = 1,
                    WorkspaceId = 1,
                    Capabilities = RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false),
                });

            Assert.False(response.Success);
            Assert.Equal(1, suppressor.Begun);
            Assert.Equal(1, suppressor.Released);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best-effort */ }
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    private sealed class RecordingSuppressor : IGitChangesRefreshSuppressor
    {
        private int _begun;
        private int _released;

        public int Begun => Volatile.Read(ref _begun);

        public int Released => Volatile.Read(ref _released);

        public IDisposable BeginExternalRepositoryMutation(string repoPath)
        {
            Interlocked.Increment(ref _begun);
            return new Scope(this);
        }

        private sealed class Scope(RecordingSuppressor owner) : IDisposable
        {
            public void Dispose() => Interlocked.Increment(ref owner._released);
        }
    }
}
