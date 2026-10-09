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
/// The Workspace-role repository's Feature worktree IS the Feature root and contains the Source worktrees, so the
/// watcher on the root spans every Source. Remove Feature deletes the Sources concurrently and then that root. Deleting
/// a Source must not overflow and re-arm the root's watcher, and the root must still be removable afterwards.
/// </summary>
public sealed class NestedFeatureRemovalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-nested-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Sources_removed_concurrently_then_the_containing_root_worktree_are_removed_with_live_watchers_and_processes()
    {
        var access = new RepositoryAccess(NullLogger<RepositoryAccess>.Instance);
        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var commandLine = new RepositoryAccessCommandLineService(inner, access);
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        var worktrees = new GitWorktreeService(runner, reader, NullLogger<GitWorktreeService>.Instance, access);

        // Special Workspace: the workspace repository's checkout holds the Source clones as ignored subfolders.
        var workspacePath = Path.Combine(_root, "ws");
        await InitRepo(workspacePath);
        await File.WriteAllTextAsync(Path.Combine(workspacePath, ".gitignore"), "SrcA/\nSrcB/\n");
        await Git(workspacePath, "add .gitignore");
        await Git(workspacePath, "commit -m ignore");

        var storageRoot = Path.Combine(_root, "features");
        var featureRoot = Path.Combine(storageRoot, "F-1");
        await Git(workspacePath, $"worktree add -b F-1 \"{featureRoot}\"");

        var sources = new List<(string Main, string Worktree)>();
        foreach (var name in new[] { "SrcA", "SrcB" })
        {
            var main = Path.Combine(workspacePath, name);
            await InitRepo(main);
            var worktree = Path.Combine(featureRoot, name);
            await Git(main, $"worktree add -b F-1 \"{worktree}\"");
            sources.Add((main, worktree));
        }

        var options = new GitChangesOptions { WatcherDebounceMilliseconds = 50 };
        using var coordinator = new GitStatusRefreshCoordinator(
            new FakeRepositoryGitChangesService { Delay = TimeSpan.Zero }, new GitChangesSnapshotCache(),
            Options.Create(options), NullLogger<GitStatusRefreshCoordinator>.Instance);
        using var manager = new GitRepositoryWatcherManager(
            coordinator, new GitChangesSnapshotCache(), new GitChangesRepositoryRegistry(),
            Options.Create(options), NullLoggerFactory.Instance, NullLogger<GitRepositoryWatcherManager>.Instance, access);
        var leases = new List<IDisposable> { manager.Acquire(featureRoot) };
        leases.AddRange(sources.Select(s => manager.Acquire(s.Worktree)));

        var sleepers = new List<Task>
        {
            commandLine.RunAsync("ping", ["-n", "60", "127.0.0.1"], featureRoot, null, CancellationToken.None, false, false, null),
        };
        sleepers.AddRange(sources.Select(s =>
            commandLine.RunAsync("ping", ["-n", "60", "127.0.0.1"], s.Worktree, null, CancellationToken.None, false, false, null)));
        await Task.Delay(500);

        // The App removes every Source in parallel, passing the Feature root and storage root for residue cleanup.
        var removedSources = await Task.WhenAll(sources.Select(s => worktrees.RemoveWorktreeAsync(
            s.Main, s.Worktree, force: false, CancellationToken.None, featureRootPath: featureRoot, featureStorageRoot: storageRoot)));
        foreach (var removed in removedSources)
        {
            Assert.True(removed.Success, removed.ErrorMessage);
            Assert.False(removed.Residue.ResidueRemaining, removed.Residue.ResidueMessage);
        }

        Assert.All(sources, s => Assert.False(Directory.Exists(s.Worktree), $"{s.Worktree} must be gone"));

        // Then the root alone; the App passes no residue paths for it because the removal deletes the directory itself.
        var rootResult = await worktrees.RemoveWorktreeAsync(workspacePath, featureRoot, force: false, CancellationToken.None);
        Assert.True(rootResult.Success, rootResult.ErrorMessage);
        Assert.False(Directory.Exists(featureRoot), "the Feature root must be gone");

        foreach (var sleeper in sleepers)
            await Assert.ThrowsAsync<PathUnderRemovalException>(() => sleeper);

        Assert.False(access.IsUnderRemoval(featureRoot));
        foreach (var lease in leases)
            lease.Dispose();
    }

    private static async Task InitRepo(string path)
    {
        Directory.CreateDirectory(path);
        await Git(path, "init");
        await Git(path, "config user.email test@example.com");
        await Git(path, "config user.name Test");
        await Git(path, "checkout -B main");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "x\n");
        await Git(path, "add README.md");
        await Git(path, "commit -m init");
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
