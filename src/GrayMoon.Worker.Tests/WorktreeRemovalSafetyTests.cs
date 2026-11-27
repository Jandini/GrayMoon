using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// What a non-force worktree removal keeps, against real git. <c>git worktree remove</c> refuses modified and
/// untracked files and submodules, and nothing else (git 2.55): a clean worktree in the middle of a rebase, merge
/// or cherry-pick, a commit only a detached HEAD holds, ignored files and a repository nested in an ignored folder
/// are all deleted with exit code 0. The first test of each kind states what git does on its own, so a change in
/// git shows here; the others hold the Worker to the guard it adds.
/// </summary>
public sealed class WorktreeRemovalSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-wrs-").FullName;
    private readonly GitProcessCounter _counter;
    private readonly GitCliRepositoryReader _reader;
    private readonly GitWorktreeService _worktrees;
    private readonly CreateGitWorktreeCommand _create;
    private readonly RemoveGitWorktreeCommand _remove;

    public WorktreeRemovalSafetyTests()
    {
        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        _counter = new GitProcessCounter(inner);
        var runner = new GitProcessRunner(_counter, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _worktrees = new GitWorktreeService(runner, _reader, NullLogger<GitWorktreeService>.Instance, new RepositoryAccess(NullLogger<RepositoryAccess>.Instance));
        var git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader, new LibGit2SharpGitIgnoreService());
        _create = new CreateGitWorktreeCommand(git, _worktrees);
        _remove = new RemoveGitWorktreeCommand(_worktrees);
    }

    public void Dispose() => TestDirectory.Delete(_root);

    // ---------------------------------------------------------------- what git does on its own

    [Fact]
    public async Task Git_itself_removes_a_clean_worktree_with_an_unfinished_cherry_pick_a_detached_commit_and_a_nested_repository()
    {
        var (main, picking) = await NewWorktreeAsync("git-pick", "feat-git-pick");
        MarkCherryPickInProgress(picking);
        Assert.Equal(0, RawGitRemove(main, picking));

        var (main2, detached) = await NewWorktreeAsync("git-detached", "feat-git-detached");
        CommitOnDetachedHead(detached);
        Assert.Equal(0, RawGitRemove(main2, detached));

        var (main3, nesting) = await NewWorktreeAsync("git-nested", "feat-git-nested", gitignore: "bin/\n");
        CreateNestedRepository(Path.Combine(nesting, "bin", "inner"));
        Assert.Equal(0, RawGitRemove(main3, nesting));
        Assert.False(Directory.Exists(nesting));
    }

    [Fact]
    public async Task Git_itself_refuses_untracked_files_and_a_lock_and_exit_zero_means_unregistered()
    {
        var (main, dirty) = await NewWorktreeAsync("git-dirty", "feat-git-dirty");
        await File.WriteAllTextAsync(Path.Combine(dirty, "work.txt"), "w\n");
        Assert.NotEqual(0, RawGitRemove(main, dirty));
        Assert.True(await IsRegisteredAsync(main, dirty));

        var (main2, locked) = await NewWorktreeAsync("git-locked", "feat-git-locked");
        GitConfigTestSupport.Git(main2, $"worktree lock \"{locked}\"");
        Assert.NotEqual(0, RawGitRemove(main2, locked));
        Assert.True(await IsRegisteredAsync(main2, locked));

        // A zero exit always leaves the worktree unregistered, also when its folder was already gone.
        var (main3, clean) = await NewWorktreeAsync("git-clean", "feat-git-clean");
        Assert.Equal(0, RawGitRemove(main3, clean));
        Assert.False(await IsRegisteredAsync(main3, clean));
        var (main4, missing) = await NewWorktreeAsync("git-missing", "feat-git-missing");
        TestDirectory.Delete(missing);
        Assert.Equal(0, RawGitRemove(main4, missing));
        Assert.False(await IsRegisteredAsync(main4, missing));
    }

    // ---------------------------------------------------------------- the Worker's removal

    [Fact]
    public async Task A_clean_worktree_is_removed_with_one_inventory_read_and_one_remove()
    {
        var (main, worktree) = await NewWorktreeAsync("clean", "feat-clean");
        _counter.Clear();

        var response = await RemoveAsync(main, worktree);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Null(response.FailureKind);
        Assert.False(Directory.Exists(worktree));
        Assert.False(await IsRegisteredAsync(main, worktree));
    }

    [Fact]
    public async Task The_guards_add_no_git_process_to_the_removal_of_a_clean_worktree_on_its_branch()
    {
        var (main, worktree) = await NewWorktreeAsync("count", "feat-count");
        _counter.Clear();

        var response = await RemoveAsync(main, worktree);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.True(_counter.Count == 2, $"Expected the inventory and the removal, saw:{Environment.NewLine}{_counter.Describe()}");
        Assert.Equal(1, _counter.CountOf("worktree list"));
        Assert.Equal(1, _counter.CountOf("worktree remove"));
    }

    [Fact]
    public async Task Ignored_files_go_with_the_worktree()
    {
        // Retention policy: files the repository declares ignored are build output and are removed with the
        // worktree, as git itself does. This test holds the decision; it is not an accident of the guard order.
        var (main, worktree) = await NewWorktreeAsync("ignored", "feat-ignored", gitignore: "bin/\n*.log\n");
        Directory.CreateDirectory(Path.Combine(worktree, "bin"));
        await File.WriteAllTextAsync(Path.Combine(worktree, "bin", "app.dll"), "built\n");
        await File.WriteAllTextAsync(Path.Combine(worktree, "build.log"), "log\n");

        var response = await RemoveAsync(main, worktree);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.False(Directory.Exists(worktree));
    }

    [Fact]
    public async Task Uncommitted_work_is_kept_and_reported_from_the_worktree_status_not_from_the_message()
    {
        var (main, worktree) = await NewWorktreeAsync("dirty", "feat-dirty");
        var untracked = Path.Combine(worktree, "work.txt");
        await File.WriteAllTextAsync(untracked, "not committed\n");
        await File.WriteAllTextAsync(Path.Combine(worktree, "README.md"), "edited\n");

        var response = await RemoveAsync(main, worktree);

        Assert.False(response.Success);
        Assert.Equal(WorktreeRemovalErrorCodes.UncommittedChanges, response.ErrorCode);
        Assert.Equal(nameof(WorktreeRemovalFailureKind.UncommittedChanges), response.FailureKind);
        Assert.Equal("not committed\n", await File.ReadAllTextAsync(untracked));
        Assert.Equal("edited\n", await File.ReadAllTextAsync(Path.Combine(worktree, "README.md")));
        Assert.True(await IsRegisteredAsync(main, worktree));
    }

    [Fact]
    public async Task Staged_work_is_kept()
    {
        var (main, worktree) = await NewWorktreeAsync("staged", "feat-staged");
        await File.WriteAllTextAsync(Path.Combine(worktree, "staged.txt"), "s\n");
        GitConfigTestSupport.Git(worktree, "add staged.txt");

        var response = await RemoveAsync(main, worktree);

        Assert.False(response.Success);
        Assert.Equal(nameof(WorktreeRemovalFailureKind.UncommittedChanges), response.FailureKind);
        Assert.True(File.Exists(Path.Combine(worktree, "staged.txt")));
    }

    [Fact]
    public async Task A_locked_worktree_is_kept_without_asking_git_and_removed_only_with_unlock()
    {
        var (main, worktree) = await NewWorktreeAsync("locked", "feat-locked");
        GitConfigTestSupport.Git(main, $"worktree lock --reason \"in use by a build\" \"{worktree}\"");
        _counter.Clear();

        var refused = await RemoveAsync(main, worktree);

        Assert.False(refused.Success);
        Assert.Equal(WorktreeRemovalErrorCodes.WorktreeLocked, refused.ErrorCode);
        Assert.Equal(nameof(WorktreeRemovalFailureKind.WorktreeLocked), refused.FailureKind);
        Assert.Contains("in use by a build", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, _counter.CountOf("worktree remove"));
        Assert.True(Directory.Exists(worktree));

        var unlocked = await RemoveAsync(main, worktree, unlock: true);
        Assert.True(unlocked.Success, unlocked.ErrorMessage);
        Assert.False(Directory.Exists(worktree));
    }

    [Theory]
    [InlineData("CHERRY_PICK_HEAD")]
    [InlineData("MERGE_HEAD")]
    public async Task A_clean_worktree_with_an_unfinished_operation_is_kept(string marker)
    {
        var (main, worktree) = await NewWorktreeAsync("op-" + marker.ToLowerInvariant().Replace('_', '-'), "feat-op-" + marker.ToLowerInvariant().Replace('_', '-'));
        var gitDir = GitDirectoryLocator.TryReadGitDirFromWorkTree(worktree)!;
        await File.WriteAllTextAsync(Path.Combine(gitDir, marker), GitConfigTestSupport.Git(worktree, "rev-parse HEAD"));

        var response = await RemoveAsync(main, worktree);

        Assert.False(response.Success);
        Assert.Equal(nameof(WorktreeRemovalFailureKind.OperationInProgress), response.FailureKind);
        Assert.True(Directory.Exists(worktree));
        Assert.True(await IsRegisteredAsync(main, worktree));
    }

    [Fact]
    public async Task A_clean_worktree_in_the_middle_of_a_real_rebase_is_kept()
    {
        var (main, worktree) = await NewWorktreeAsync("rebase", "feat-rebase");
        await File.WriteAllTextAsync(Path.Combine(main, "main.txt"), "m\n");
        GitConfigTestSupport.Git(main, "add -A");
        GitConfigTestSupport.Git(main, "commit -m main-moves");
        await File.WriteAllTextAsync(Path.Combine(worktree, "README.md"), "feature\n");
        GitConfigTestSupport.Git(worktree, "commit -am feature-change");
        await File.WriteAllTextAsync(Path.Combine(main, "README.md"), "main\n");
        GitConfigTestSupport.Git(main, "commit -am main-change");
        // The rebase stops on the conflict; resolving it leaves a clean tree with the rebase still open.
        GitConfigTestSupport.Git(worktree, "rebase main", allowFailure: true);
        await File.WriteAllTextAsync(Path.Combine(worktree, "README.md"), "main\n");
        GitConfigTestSupport.Git(worktree, "add README.md");
        var gitDir = GitDirectoryLocator.TryReadGitDirFromWorkTree(worktree)!;
        Assert.True(Directory.Exists(Path.Combine(gitDir, "rebase-merge")) || Directory.Exists(Path.Combine(gitDir, "rebase-apply")));

        var response = await RemoveAsync(main, worktree);

        Assert.False(response.Success);
        Assert.Contains(response.FailureKind, new[] { nameof(WorktreeRemovalFailureKind.OperationInProgress), nameof(WorktreeRemovalFailureKind.UncommittedChanges) });
        Assert.True(Directory.Exists(worktree));
    }

    [Fact]
    public async Task A_commit_only_the_detached_HEAD_holds_keeps_the_worktree_also_with_force()
    {
        var (main, worktree) = await NewWorktreeAsync("detached", "feat-detached");
        var lost = CommitOnDetachedHead(worktree);

        var refused = await RemoveAsync(main, worktree);
        var refusedWithForce = await RemoveAsync(main, worktree, force: true);

        Assert.False(refused.Success);
        Assert.Equal(nameof(WorktreeRemovalFailureKind.UnpreservedCommits), refused.FailureKind);
        Assert.Contains(lost[..10], refused.ErrorMessage, StringComparison.Ordinal);
        Assert.False(refusedWithForce.Success);
        Assert.Equal(nameof(WorktreeRemovalFailureKind.UnpreservedCommits), refusedWithForce.FailureKind);
        Assert.True(Directory.Exists(worktree));

        // Once a branch holds the commit there is nothing to lose and the same removal goes through.
        GitConfigTestSupport.Git(worktree, "branch rescued");
        var removed = await RemoveAsync(main, worktree);
        Assert.True(removed.Success, removed.ErrorMessage);
        Assert.Equal(lost, GitConfigTestSupport.Git(main, "rev-parse rescued").Trim());
    }

    [Fact]
    public async Task A_detached_worktree_on_a_commit_a_branch_holds_is_removed()
    {
        // A tag-pinned repository and a read only snapshot are detached by design, at a commit that is on a ref.
        var (main, worktree) = await NewWorktreeAsync("pinned", "feat-pinned");
        GitConfigTestSupport.Git(worktree, "checkout --quiet --detach main");

        var response = await RemoveAsync(main, worktree);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.False(Directory.Exists(worktree));
    }

    [Fact]
    public async Task A_repository_nested_in_an_ignored_folder_keeps_the_worktree_also_with_force()
    {
        var (main, worktree) = await NewWorktreeAsync("nested", "feat-nested", gitignore: "scratch/\n");
        var nested = Path.Combine(worktree, "scratch", "experiment");
        CreateNestedRepository(nested);

        var refused = await RemoveAsync(main, worktree);
        var refusedWithForce = await RemoveAsync(main, worktree, force: true);

        Assert.False(refused.Success);
        Assert.Equal(nameof(WorktreeRemovalFailureKind.NestedRepository), refused.FailureKind);
        Assert.Contains(Path.Combine("scratch", "experiment"), refused.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(nameof(WorktreeRemovalFailureKind.NestedRepository), refusedWithForce.FailureKind);
        Assert.True(Directory.Exists(Path.Combine(nested, ".git")));
        Assert.True(await IsRegisteredAsync(main, worktree));
    }

    [Fact]
    public async Task An_untracked_nested_repository_keeps_the_worktree()
    {
        var (main, worktree) = await NewWorktreeAsync("nested-untracked", "feat-nested-untracked");
        CreateNestedRepository(Path.Combine(worktree, "inner"));

        var response = await RemoveAsync(main, worktree);

        Assert.False(response.Success);
        Assert.Equal(nameof(WorktreeRemovalFailureKind.NestedRepository), response.FailureKind);
        Assert.True(Directory.Exists(Path.Combine(worktree, "inner", ".git")));
    }

    [Fact]
    public async Task A_worktree_with_a_submodule_is_kept()
    {
        var (main, worktree) = await NewWorktreeAsync("submodule", "feat-submodule");
        var library = Path.Combine(_root, "library");
        GitScenarios.CommittedRepository(library);
        GitConfigTestSupport.Git(worktree, $"-c protocol.file.allow=always submodule add \"{library.Replace('\\', '/')}\" lib");
        GitConfigTestSupport.Git(worktree, "commit -m add-submodule");

        var response = await RemoveAsync(main, worktree);

        Assert.False(response.Success);
        Assert.Equal(nameof(WorktreeRemovalFailureKind.NestedRepository), response.FailureKind);
        Assert.True(File.Exists(Path.Combine(worktree, "lib", "README.md")));
        Assert.True(await IsRegisteredAsync(main, worktree));
    }

    [Fact]
    public async Task The_primary_checkout_is_never_removed()
    {
        var (main, _) = await NewWorktreeAsync("primary", "feat-primary");

        var response = await RemoveAsync(main, main, force: true);

        Assert.False(response.Success);
        Assert.Equal("CannotRemovePrimary", response.ErrorCode);
        Assert.True(File.Exists(Path.Combine(main, "README.md")));
    }

    [Fact]
    public async Task A_path_that_is_not_registered_is_reported_as_already_removed_and_a_foreign_folder_there_is_left_alone()
    {
        var (main, _) = await NewWorktreeAsync("foreign", "feat-foreign");
        var foreign = Path.Combine(_root, "features", "foreign", "not-a-worktree");
        Directory.CreateDirectory(foreign);
        await File.WriteAllTextAsync(Path.Combine(foreign, "mine.txt"), "someone's file\n");

        // No Feature storage root is given, as for any path GrayMoon does not manage: nothing may be deleted.
        var response = await RemoveAsync(main, foreign);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.True(response.AlreadyRemoved);
        Assert.True(response.ResidueRemaining);
        Assert.Equal("someone's file\n", await File.ReadAllTextAsync(Path.Combine(foreign, "mine.txt")));
    }

    [Fact]
    public async Task A_worktree_whose_folder_is_already_gone_is_unregistered()
    {
        var (main, worktree) = await NewWorktreeAsync("missing", "feat-missing");
        TestDirectory.Delete(worktree);

        var response = await RemoveAsync(main, worktree);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.False(await IsRegisteredAsync(main, worktree));
    }

    [Fact]
    public async Task A_removal_that_already_happened_answers_already_removed_so_a_lost_reply_can_be_asked_again()
    {
        var (main, worktree) = await NewWorktreeAsync("again", "feat-again");
        var first = await RemoveAsync(main, worktree);
        Assert.True(first.Success, first.ErrorMessage);
        Assert.False(first.AlreadyRemoved);

        _counter.Clear();
        var second = await RemoveAsync(main, worktree);

        Assert.True(second.Success, second.ErrorMessage);
        Assert.True(second.AlreadyRemoved);
        Assert.Equal(0, _counter.CountOf("worktree remove"));
    }

    [Fact]
    public void Nested_repository_search_ignores_the_worktrees_own_git_file_and_does_not_follow_links()
    {
        var folder = Path.Combine(_root, "search");
        Directory.CreateDirectory(Path.Combine(folder, "src", "deep"));
        File.WriteAllText(Path.Combine(folder, ".git"), "gitdir: elsewhere\n");
        Assert.Null(GitWorktreeService.FindNestedRepository(folder));

        File.WriteAllText(Path.Combine(folder, "src", "deep", ".git"), "gitdir: elsewhere\n");
        Assert.Equal(Path.Combine("src", "deep"), GitWorktreeService.FindNestedRepository(folder));
    }

    [Fact]
    public async Task A_forced_removal_discards_the_reviewed_changes_of_a_worktree_that_is_still_on_the_reviewed_commit()
    {
        var (main, worktree) = await NewWorktreeAsync("force-same", "feat-force-same");
        var reviewedHead = GitConfigTestSupport.Git(worktree, "rev-parse HEAD").Trim();
        await File.WriteAllTextAsync(Path.Combine(worktree, "work.txt"), "reviewed and given up\n");

        var response = await RemoveAsync(main, worktree, force: true, expectedHeadSha: reviewedHead);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.False(Directory.Exists(worktree));
    }

    [Fact]
    public async Task A_forced_removal_keeps_a_worktree_that_got_a_commit_after_the_review()
    {
        // The discard was authorized for what the dialog listed. A commit made since is not part of that, and the
        // files beside it are no longer the ones that were reviewed.
        var (main, worktree) = await NewWorktreeAsync("force-moved", "feat-force-moved");
        var reviewedHead = GitConfigTestSupport.Git(worktree, "rev-parse HEAD").Trim();
        await File.WriteAllTextAsync(Path.Combine(worktree, "later.txt"), "after the review\n");
        GitConfigTestSupport.Git(worktree, "add later.txt");
        GitConfigTestSupport.Git(worktree, "commit -m later");
        await File.WriteAllTextAsync(Path.Combine(worktree, "work.txt"), "new work\n");

        var response = await RemoveAsync(main, worktree, force: true, expectedHeadSha: reviewedHead);

        Assert.False(response.Success);
        Assert.Equal(nameof(WorktreeRemovalFailureKind.ChangedSinceReview), response.FailureKind);
        Assert.Equal("new work\n", await File.ReadAllTextAsync(Path.Combine(worktree, "work.txt")));
        Assert.True(await IsRegisteredAsync(main, worktree));
    }

    private Task<Jobs.Response.RemoveGitWorktreeResponse> RemoveAsync(
        string main, string worktree, bool force = false, bool unlock = false, string? expectedHeadSha = null)
        => _remove.ExecuteAsync(new RemoveGitWorktreeRequest
        {
            MainRepositoryPath = main,
            WorktreePath = worktree,
            Force = force,
            Unlock = unlock,
            ExpectedHeadSha = expectedHeadSha,
        });

    private async Task<(string Main, string Worktree)> NewWorktreeAsync(string name, string branch, string? gitignore = null)
    {
        var main = Path.Combine(_root, name);
        GitScenarios.CommittedRepository(main);
        if (gitignore is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(main, ".gitignore"), gitignore);
            GitConfigTestSupport.Git(main, "add .gitignore");
            GitConfigTestSupport.Git(main, "commit -m ignore");
        }

        var head = (await _reader.GetHeadCommitAsync(main, CancellationToken.None))!;
        var worktree = Path.Combine(_root, "features", name, "repo");
        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = main,
            WorktreePath = worktree,
            BranchName = branch,
            BaseCommitSha = head,
            DivergenceBaseBranch = "main",
        });
        Assert.True(created.Success, created.ErrorMessage);
        return (main, worktree);
    }

    private async Task<bool> IsRegisteredAsync(string main, string worktree)
    {
        var (ok, worktrees, _, error) = await _worktrees.ListWorktreesAsync(main, CancellationToken.None);
        Assert.True(ok, error);
        return GitWorktreeOccupancy.FindByPath(worktrees, Path.GetFullPath(worktree)) != null;
    }

    /// <summary><c>git worktree remove</c> without the Worker, to state what git does on its own. Returns the exit code.</summary>
    private static int RawGitRemove(string main, string worktree)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", $"worktree remove \"{worktree}\"")
        {
            WorkingDirectory = main,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void MarkCherryPickInProgress(string worktree)
    {
        var gitDir = GitDirectoryLocator.TryReadGitDirFromWorkTree(worktree)!;
        File.WriteAllText(Path.Combine(gitDir, "CHERRY_PICK_HEAD"), GitConfigTestSupport.Git(worktree, "rev-parse HEAD"));
    }

    /// <summary>Detaches HEAD and commits there, so the new commit is on no branch. Returns its SHA.</summary>
    private static string CommitOnDetachedHead(string worktree)
    {
        GitConfigTestSupport.Git(worktree, "checkout --quiet --detach");
        File.WriteAllText(Path.Combine(worktree, "detached.txt"), "only here\n");
        GitConfigTestSupport.Git(worktree, "add detached.txt");
        GitConfigTestSupport.Git(worktree, "commit -m detached-work");
        return GitConfigTestSupport.Git(worktree, "rev-parse HEAD").Trim();
    }

    private static void CreateNestedRepository(string path)
    {
        GitScenarios.CommittedRepository(path, new CommitSpec(File: "inner.txt", Content: "inner\n", Message: "inner"));
    }
}
