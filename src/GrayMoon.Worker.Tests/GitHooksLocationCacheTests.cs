using System.Collections.Concurrent;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Unit 2 of the Sync critical path work: once git has resolved a repository's hooks location, an unchanged
/// repository must not start another git process for it. These tests pin the process count and every way the
/// cached answer has to be dropped. Real git, real repositories.
/// </summary>
public sealed class GitHooksLocationCacheTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-hookcache-").FullName;
    private readonly GitService _git;

    public GitHooksLocationCacheTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());
    }

    public void Dispose()
    {
        try { ForceDelete(_root); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task An_unchanged_repository_needs_no_git_process_after_the_first_install()
    {
        var repo = await InitAsync("repo");

        Assert.Equal(1, await InstallCountingAsync(repo));
        var pushHook = Path.Combine(repo, ".git", "hooks", "pre-push");
        Assert.True(File.Exists(pushHook));
        var written = File.GetLastWriteTimeUtc(pushHook);

        Assert.Equal(0, await InstallCountingAsync(repo));
        Assert.Equal(0, await InstallCountingAsync(repo));
        Assert.Equal(written, File.GetLastWriteTimeUtc(pushHook)); // not rewritten
    }

    [Fact]
    public async Task Changing_core_hooksPath_to_a_folder_inside_the_git_directory_moves_the_install()
    {
        var repo = await InitAsync("repo");
        await InstallCountingAsync(repo);
        Assert.Equal(0, await InstallCountingAsync(repo));

        var inside = Directory.CreateDirectory(Path.Combine(repo, ".git", "my-hooks")).FullName;
        await GitAsync(repo, $"config core.hooksPath \"{inside.Replace('\\', '/')}\"");

        Assert.Equal(1, await InstallCountingAsync(repo));
        Assert.True(File.Exists(Path.Combine(inside, "pre-push")));
        Assert.Equal(0, await InstallCountingAsync(repo));
    }

    [Fact]
    public async Task A_hooksPath_outside_the_git_directory_is_remembered_and_never_written_to()
    {
        var repo = await InitAsync("repo");
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside-hooks")).FullName;
        await GitAsync(repo, $"config core.hooksPath \"{outside.Replace('\\', '/')}\"");

        Assert.Equal(1, await InstallCountingAsync(repo)); // rev-parse; the config value is read in-process
        Assert.Equal(0, await InstallCountingAsync(repo));
        Assert.Empty(Directory.GetFiles(outside));

        // Taking the setting away brings the install back to the default hooks folder.
        await GitAsync(repo, "config --unset core.hooksPath");
        Assert.Equal(1, await InstallCountingAsync(repo));
        Assert.True(File.Exists(Path.Combine(repo, ".git", "hooks", "pre-push")));
    }

    [Fact]
    public async Task A_relative_hooksPath_is_remembered_and_follows_changes_to_it()
    {
        var repo = await InitAsync("repo");
        await GitAsync(repo, "config core.hooksPath .githooks");

        Assert.Equal(1, await InstallCountingAsync(repo)); // rev-parse only; core.hooksPath is read in-process
        Assert.Equal(0, await InstallCountingAsync(repo));
        Assert.False(Directory.Exists(Path.Combine(repo, ".githooks")));

        await GitAsync(repo, "config core.hooksPath .other");
        Assert.Equal(1, await InstallCountingAsync(repo)); // rev-parse only; core.hooksPath is read in-process
        Assert.Equal(0, await InstallCountingAsync(repo));
    }

    [Fact]
    public async Task A_linked_worktree_installs_into_the_common_hooks_folder_and_is_cached_on_its_own()
    {
        var repo = await InitAsync("repo");
        var worktree = Path.Combine(_root, "wt");
        await GitAsync(repo, $"worktree add \"{worktree}\" -b feat");

        Assert.Equal(1, await InstallCountingAsync(worktree));
        Assert.True(File.Exists(Path.Combine(repo, ".git", "hooks", "pre-push")));
        Assert.False(Directory.Exists(Path.Combine(repo, ".git", "worktrees", "wt", "hooks")));
        Assert.Equal(0, await InstallCountingAsync(worktree));

        // The main checkout is its own cache entry: one more answer from git, then none.
        Assert.Equal(1, await InstallCountingAsync(repo));
        Assert.Equal(0, await InstallCountingAsync(repo));
    }

    [Fact]
    public async Task A_removed_and_re_added_worktree_still_installs_into_the_common_hooks_folder()
    {
        var repo = await InitAsync("repo");
        var worktree = Path.Combine(_root, "wt");
        await GitAsync(repo, $"worktree add \"{worktree}\" -b feat");
        await InstallCountingAsync(worktree);
        Assert.Equal(0, await InstallCountingAsync(worktree));

        await GitAsync(repo, $"worktree remove --force \"{worktree}\"");
        await GitAsync(repo, $"worktree add \"{worktree}\" -b feat2");

        await InstallCountingAsync(worktree);
        Assert.True(File.Exists(Path.Combine(repo, ".git", "hooks", "pre-push")));
        Assert.Equal(0, await InstallCountingAsync(worktree));
    }

    [Fact]
    public async Task A_config_that_includes_other_files_always_asks_git()
    {
        var repo = await InitAsync("repo");
        var extra = Path.Combine(_root, "extra.gitconfig");
        await File.WriteAllTextAsync(extra, "[user]\n\tname = Someone\n");
        await GitAsync(repo, $"config include.path \"{extra.Replace('\\', '/')}\"");

        Assert.Equal(1, await InstallCountingAsync(repo));
        Assert.Equal(1, await InstallCountingAsync(repo));

        // The included file is where the hooks path can then come from, and it is honoured on the next call.
        var outside = Directory.CreateDirectory(Path.Combine(_root, "included-hooks")).FullName;
        await File.WriteAllTextAsync(extra, $"[core]\n\thooksPath = {outside.Replace('\\', '/')}\n");
        Assert.Equal(1, await InstallCountingAsync(repo)); // rev-parse only; core.hooksPath is read in-process
        Assert.Empty(Directory.GetFiles(outside));
    }

    [Fact]
    public async Task A_repository_recreated_at_the_same_path_is_resolved_again()
    {
        var repo = await InitAsync("repo");
        await InstallCountingAsync(repo);
        Assert.Equal(0, await InstallCountingAsync(repo));

        ForceDelete(repo);
        await InitAsync("repo");

        Assert.Equal(1, await InstallCountingAsync(repo));
        Assert.True(File.Exists(Path.Combine(repo, ".git", "hooks", "pre-push")));
    }

    [Fact]
    public async Task A_failed_resolution_is_not_remembered()
    {
        var notYet = Directory.CreateDirectory(Path.Combine(_root, "not-yet")).FullName;
        await InstallCountingAsync(notYet); // not a repository: nothing installed, nothing cached
        Assert.False(Directory.Exists(Path.Combine(notYet, ".git")));

        await GitAsync(notYet, "init -b main");

        Assert.Equal(1, await InstallCountingAsync(notYet));
        Assert.True(File.Exists(Path.Combine(notYet, ".git", "hooks", "pre-push")));
    }

    [Fact]
    public async Task Concurrent_installs_for_one_repository_all_succeed_and_settle_to_zero_processes()
    {
        var repo = await InitAsync("repo");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _git.WriteSyncHooksAsync(repo, 1, 2, CancellationToken.None)));

        Assert.True(File.Exists(Path.Combine(repo, ".git", "hooks", "pre-push")));
        Assert.Equal(0, await InstallCountingAsync(repo));
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsGit(CommandLineStreamEvent e)
        => e.Kind == WorkerCommandStreamKind.CommandLine && e.Text.StartsWith("$ git", StringComparison.Ordinal);

    /// <summary>Installs the sync hooks and returns how many git processes that started.</summary>
    private async Task<int> InstallCountingAsync(string repo)
    {
        var events = new ConcurrentQueue<CommandLineStreamEvent>();
        using (new CommandLineStreamScope(events.Enqueue))
            await _git.WriteSyncHooksAsync(repo, 1, 2, CancellationToken.None);
        return events.Count(IsGit);
    }

    private async Task<string> InitAsync(string name)
    {
        var repo = Path.Combine(_root, name);
        Directory.CreateDirectory(repo);
        await GitAsync(repo, "init -b main");
        await GitAsync(repo, "config user.email t@example.com");
        await GitAsync(repo, "config user.name T");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "init\n");
        await GitAsync(repo, "add -A");
        await GitAsync(repo, "commit -m init");
        return repo;
    }

    private static void ForceDelete(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }

    private static async Task<string> GitAsync(string workingDirectory, string args)
    {
        var (exit, stdout, stderr) = await GitVersionParityTests.RunProcessAsync("git", args, workingDirectory);
        if (exit != 0)
            throw new InvalidOperationException($"git {args} failed in {workingDirectory}: {stderr}");
        return stdout;
    }
}
