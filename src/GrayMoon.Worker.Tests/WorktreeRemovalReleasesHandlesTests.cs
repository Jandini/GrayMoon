using System.Diagnostics;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services;
using GrayMoon.Worker.Services.GitChanges;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// End to end: a real linked worktree with a live file system watcher and a Worker-started process running inside it is
/// removed. Everything the Worker holds must be let go so the folder really disappears, and the removal must not wait
/// for the process to finish by itself.
/// </summary>
public sealed class WorktreeRemovalReleasesHandlesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-handles-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Remove_releases_watcher_and_kills_worker_started_process_so_the_folder_is_deleted()
    {
        var access = new RepositoryAccess(NullLogger<RepositoryAccess>.Instance);
        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var commandLine = new RepositoryAccessCommandLineService(inner, access);
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        var worktrees = new GitWorktreeService(runner, reader, NullLogger<GitWorktreeService>.Instance, access);

        var mainPath = Path.Combine(_root, "main");
        Directory.CreateDirectory(mainPath);
        await Git(mainPath, "init");
        await Git(mainPath, "config user.email test@example.com");
        await Git(mainPath, "config user.name Test");
        await Git(mainPath, "checkout -B main");
        await File.WriteAllTextAsync(Path.Combine(mainPath, "README.md"), "x\n");
        await Git(mainPath, "add README.md");
        await Git(mainPath, "commit -m init");
        var head = (await reader.GetHeadCommitAsync(mainPath, CancellationToken.None))!;

        var worktreePath = Path.Combine(_root, "features", "F-1", "main");
        await Git(mainPath, $"worktree add -b F-1 \"{worktreePath}\" {head}");

        var options = new GitChangesOptions { WatcherDebounceMilliseconds = 50 };
        using var coordinator = new GitStatusRefreshCoordinator(
            new FakeRepositoryGitChangesService { Delay = TimeSpan.Zero }, new GitChangesSnapshotCache(),
            Options.Create(options), NullLogger<GitStatusRefreshCoordinator>.Instance);
        using var manager = new GitRepositoryWatcherManager(
            coordinator, new GitChangesSnapshotCache(), new GitChangesRepositoryRegistry(),
            Options.Create(options), NullLoggerFactory.Instance, NullLogger<GitRepositoryWatcherManager>.Instance, access);
        using var watcherLease = manager.Acquire(worktreePath);
        Assert.True(manager.TryGetCoverage(worktreePath, out _));

        // A process the Worker started inside the worktree, which would pin the folder for as long as it runs.
        var sleeper = commandLine.RunAsync(
            "ping", ["-n", "60", "127.0.0.1"], worktreePath, null, CancellationToken.None, false, false, null);
        await Task.Delay(500);
        Assert.False(sleeper.IsCompleted);

        var sw = Stopwatch.StartNew();
        var removed = await worktrees.RemoveWorktreeAsync(mainPath, worktreePath, force: true, CancellationToken.None);
        sw.Stop();

        Assert.True(removed.Success, removed.ErrorMessage);
        Assert.False(Directory.Exists(worktreePath), "the worktree folder must be gone");
        Assert.False(manager.TryGetCoverage(worktreePath, out _), "the watcher must have been released");
        await Assert.ThrowsAsync<PathUnderRemovalException>(() => sleeper);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"removal took {sw.Elapsed}");

        // Nothing may start there while the claim is held, and the claim is gone afterwards.
        Assert.False(access.IsUnderRemoval(worktreePath));
    }

    private static async Task Git(string workingDirectory, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {arguments}: {await stderr}");
    }
}
