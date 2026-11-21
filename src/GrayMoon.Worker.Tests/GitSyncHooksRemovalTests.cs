using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Removing GrayMoon's sync hooks: only files with GrayMoon's marker go, the user's own replaced hook comes back, and
/// nothing outside the common Git directory is looked at.
/// </summary>
[Trait("Category", "PullRequest")]
public sealed class GitSyncHooksRemovalTests : IDisposable
{
    private const string Marker = "# Created by GrayMoon.Agent";
    private const string ReplacedSuffix = ".replaced-by-graymoon";
    private const string OwnHook = "#!/bin/sh\necho mine\n";

    private static readonly string[] HookNames = ["post-commit", "post-checkout", "post-merge", "pre-push", "reference-transaction"];

    private readonly string _root = TestDirectory.Create("hook-removal");
    private readonly string _repo;
    private readonly GitService _git;

    public GitSyncHooksRemovalTests()
    {
        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(inner, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());
        _repo = Path.Combine(_root, "repo");
        GitScenarios.CommittedRepository(_repo);
    }

    public void Dispose() => TestDirectory.Delete(_root);

    private string HooksDir => Path.Combine(_repo, ".git", "hooks");

    private string HookPath(string name) => Path.Combine(HooksDir, name);

    private Task InstallAsync(string? path = null) => _git.WriteSyncHooksAsync(path ?? _repo, 1, 2, CancellationToken.None);

    private async Task<SyncHookRemovalResult> RemoveAsync(string? path = null) =>
        _git.RemoveSyncHooks(await _git.ResolveSyncHookRemovalAsync(path ?? _repo, CancellationToken.None));

    private static bool IsGrayMoonHook(string path) => File.Exists(path) && File.ReadAllText(path).Contains(Marker, StringComparison.Ordinal);

    [Fact]
    public async Task Installed_hooks_are_removed_and_a_second_removal_finds_nothing()
    {
        await InstallAsync();
        Assert.All(HookNames, name => Assert.True(IsGrayMoonHook(HookPath(name)), name));

        var first = await RemoveAsync();

        Assert.Equal(SyncHookRemovalOutcome.Removed, first.Outcome);
        Assert.Equal(HookNames.Order(), first.RemovedHooks.Order());
        Assert.Empty(first.RestoredHooks);
        Assert.All(HookNames, name => Assert.False(File.Exists(HookPath(name)), name));

        var second = await RemoveAsync();
        Assert.Equal(SyncHookRemovalOutcome.NothingToRemove, second.Outcome);
        Assert.Empty(second.RemovedHooks);
    }

    [Fact]
    public async Task The_users_own_hook_that_install_moved_aside_is_put_back_with_its_content()
    {
        Directory.CreateDirectory(HooksDir);
        File.WriteAllText(HookPath("post-commit"), OwnHook);
        await InstallAsync();
        Assert.True(File.Exists(HookPath("post-commit") + ReplacedSuffix));

        var result = await RemoveAsync();

        Assert.Equal(SyncHookRemovalOutcome.Removed, result.Outcome);
        Assert.Equal(["post-commit"], result.RestoredHooks);
        Assert.Equal(OwnHook, File.ReadAllText(HookPath("post-commit")));
        Assert.False(File.Exists(HookPath("post-commit") + ReplacedSuffix));
        Assert.Empty(result.LeftoverReplacedHooks);
    }

    [Fact]
    public async Task With_several_replaced_files_the_newest_is_put_back_and_the_others_are_reported()
    {
        await InstallAsync();
        File.WriteAllText(HookPath("pre-push") + ReplacedSuffix, "#!/bin/sh\necho oldest\n");
        File.WriteAllText(HookPath("pre-push") + ReplacedSuffix + "-20260101000000", "#!/bin/sh\necho middle\n");
        File.WriteAllText(HookPath("pre-push") + ReplacedSuffix + "-20260301120000", "#!/bin/sh\necho newest\n");

        var result = await RemoveAsync();

        Assert.Equal(SyncHookRemovalOutcome.Removed, result.Outcome);
        Assert.Contains("pre-push", result.RestoredHooks);
        Assert.Equal("#!/bin/sh\necho newest\n", File.ReadAllText(HookPath("pre-push")));
        Assert.Equal(
            new[] { "pre-push" + ReplacedSuffix, "pre-push" + ReplacedSuffix + "-20260101000000" }.Order(),
            result.LeftoverReplacedHooks.Order());
        Assert.True(File.Exists(HookPath("pre-push") + ReplacedSuffix));
        Assert.True(File.Exists(HookPath("pre-push") + ReplacedSuffix + "-20260101000000"));
    }

    [Fact]
    public async Task A_hook_that_is_not_GrayMoons_is_left_exactly_as_it_is()
    {
        await InstallAsync();
        // The user put their own hook back over GrayMoon's after the install, and has one GrayMoon never installs.
        File.WriteAllText(HookPath("post-merge"), OwnHook);
        File.WriteAllText(HookPath("pre-commit"), OwnHook);

        var result = await RemoveAsync();

        Assert.Equal(SyncHookRemovalOutcome.Removed, result.Outcome);
        Assert.Equal(["post-merge"], result.ForeignHooks);
        Assert.DoesNotContain("post-merge", result.RemovedHooks);
        Assert.Equal(OwnHook, File.ReadAllText(HookPath("post-merge")));
        Assert.Equal(OwnHook, File.ReadAllText(HookPath("pre-commit")));
    }

    [Fact]
    public async Task Hooks_written_before_core_hooksPath_was_set_inside_the_git_directory_are_removed_from_both_folders()
    {
        await InstallAsync();
        var custom = Path.Combine(_repo, ".git", "custom-hooks");
        Directory.CreateDirectory(custom);
        GitConfigTestSupport.Git(_repo, "config core.hooksPath .git/custom-hooks");
        await InstallAsync();
        Assert.True(IsGrayMoonHook(Path.Combine(custom, "post-commit")));
        Assert.True(IsGrayMoonHook(HookPath("post-commit")));

        var result = await RemoveAsync();

        Assert.Equal(SyncHookRemovalOutcome.Removed, result.Outcome);
        Assert.All(HookNames, name =>
        {
            Assert.False(File.Exists(HookPath(name)), name);
            Assert.False(File.Exists(Path.Combine(custom, name)), name);
        });
    }

    [Fact]
    public async Task A_hooks_folder_outside_the_git_directory_is_never_touched()
    {
        await InstallAsync();
        var outside = Path.Combine(_root, "shared-hooks");
        Directory.CreateDirectory(outside);
        // Looks like GrayMoon's, but it is outside the repository's git directory: not ours to delete.
        File.Copy(HookPath("post-commit"), Path.Combine(outside, "post-commit"));
        GitConfigTestSupport.Git(_repo, $"config core.hooksPath \"{outside.Replace('\\', '/')}\"");

        var result = await RemoveAsync();

        Assert.Equal(SyncHookRemovalOutcome.Removed, result.Outcome);
        Assert.True(IsGrayMoonHook(Path.Combine(outside, "post-commit")));
        Assert.All(HookNames, name => Assert.False(File.Exists(HookPath(name)), name));
    }

    [Fact]
    public async Task Removing_through_a_linked_worktree_removes_the_shared_hooks_of_the_main_checkout()
    {
        await InstallAsync();
        var worktree = Path.Combine(_root, "features", "F-1", "repo");
        GitConfigTestSupport.Git(_repo, $"worktree add -b F-1 \"{worktree}\"");

        var result = await RemoveAsync(worktree);

        Assert.Equal(SyncHookRemovalOutcome.Removed, result.Outcome);
        Assert.All(HookNames, name => Assert.False(File.Exists(HookPath(name)), name));
    }

    [Fact]
    public async Task A_missing_folder_and_a_folder_that_is_not_a_repository_are_successes()
    {
        var missing = await RemoveAsync(Path.Combine(_root, "not-there"));
        Assert.Equal(SyncHookRemovalOutcome.NotFound, missing.Outcome);
        Assert.True(missing.Succeeded);

        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        var notRepository = await RemoveAsync(plain);
        Assert.Equal(SyncHookRemovalOutcome.NotARepository, notRepository.Outcome);
        Assert.True(notRepository.Succeeded);
    }

    [Fact]
    public async Task A_hook_file_that_cannot_be_deleted_fails_with_its_path_and_keeps_the_file()
    {
        if (!OperatingSystem.IsWindows())
            return; // An open handle does not block a delete on Linux.

        await InstallAsync();
        SyncHookRemovalResult result;
        using (new FileStream(HookPath("post-merge"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await RemoveAsync();
        }

        Assert.Equal(SyncHookRemovalOutcome.Failed, result.Outcome);
        Assert.Equal(SyncHookRemovalFailureKind.HookNotDeleted, result.FailureKind);
        Assert.Equal(HookPath("post-merge"), result.FailedPath, ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.True(IsGrayMoonHook(HookPath("post-merge")));

        // The retry finishes the job.
        var retry = await RemoveAsync();
        Assert.Equal(SyncHookRemovalOutcome.Removed, retry.Outcome);
        Assert.All(HookNames, name => Assert.False(File.Exists(HookPath(name)), name));
    }

    [Fact]
    public async Task After_removal_the_next_install_writes_the_hooks_again()
    {
        await InstallAsync();
        await RemoveAsync();

        await InstallAsync();

        Assert.All(HookNames, name => Assert.True(IsGrayMoonHook(HookPath(name)), name));
    }

    [Fact]
    public async Task A_path_outside_the_known_roots_is_no_longer_vouched_for_once_its_hooks_are_removed()
    {
        await _git.WriteSyncHooksAsync(_repo, 7, 42, CancellationToken.None);
        var policy = new HookRepositoryPathPolicy(new KnownWorkspaceRoots());
        Assert.Null(policy.GetRejectionReason(_repo, 7, 42));

        await RemoveAsync();

        Assert.NotNull(policy.GetRejectionReason(_repo, 7, 42));
    }
}
