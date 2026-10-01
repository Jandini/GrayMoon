using GrayMoon.Agent.Commands;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Tests;

public sealed class InspectWorktreeCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-iw-").FullName;
    private readonly GitService _git;
    private readonly CreateGitWorktreeCommand _create;
    private readonly InspectWorktreeCommand _inspect;

    public InspectWorktreeCommandTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _git = new GitService(Options.Create(new AgentOptions()), NullLogger<GitService>.Instance, runner);
        _create = new CreateGitWorktreeCommand(_git);
        _inspect = new InspectWorktreeCommand(_git);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Clean_worktree_reports_registered_exists_and_zero_counts()
    {
        var mainPath = Path.Combine(_root, "main1");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _git.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var worktreePath = Path.Combine(_root, "features", "clean", "main1");
        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "clean-feat",
            BaseCommitSha = head,
        });
        Assert.True(created.Success, created.ErrorMessage);

        var result = await _inspect.ExecuteAsync(new InspectWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            DefaultBranch = "main",
        });

        Assert.Null(result.Error);
        Assert.True(result.IsRegistered);
        Assert.True(result.Exists);
        Assert.False(result.IsLocked);
        Assert.Null(result.LockReason);
        Assert.Equal("clean-feat", result.Branch);
        Assert.Equal(head, result.HeadSha, StringComparer.OrdinalIgnoreCase);
        Assert.False(result.IsDirty);
        Assert.Equal(0, result.StagedCount);
        Assert.Equal(0, result.UnstagedCount);
        Assert.Equal(0, result.UntrackedCount);
        Assert.Equal(0, result.ConflictCount);
    }

    [Fact]
    public async Task Untracked_file_is_counted_and_marks_dirty()
    {
        var mainPath = Path.Combine(_root, "main2");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _git.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var worktreePath = Path.Combine(_root, "features", "untracked", "main2");
        await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "untracked-feat",
            BaseCommitSha = head,
        });
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "new.txt"), "x\n");

        var result = await _inspect.ExecuteAsync(new InspectWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
        });

        Assert.True(result.IsDirty);
        Assert.Equal(1, result.UntrackedCount);
        Assert.Equal(0, result.StagedCount);
        Assert.Equal(0, result.UnstagedCount);
        Assert.Equal(0, result.ConflictCount);
    }

    [Fact]
    public async Task Staged_file_is_counted_and_marks_dirty()
    {
        var mainPath = Path.Combine(_root, "main3");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _git.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var worktreePath = Path.Combine(_root, "features", "staged", "main3");
        await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "staged-feat",
            BaseCommitSha = head,
        });
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "new.txt"), "x\n");
        await RunGitAsync(worktreePath, "add new.txt");

        var result = await _inspect.ExecuteAsync(new InspectWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
        });

        Assert.True(result.IsDirty);
        Assert.Equal(1, result.StagedCount);
        Assert.Equal(0, result.UnstagedCount);
        Assert.Equal(0, result.UntrackedCount);
        Assert.Equal(0, result.ConflictCount);
    }

    [Fact]
    public async Task Unstaged_change_is_counted_and_marks_dirty()
    {
        var mainPath = Path.Combine(_root, "main4");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _git.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var worktreePath = Path.Combine(_root, "features", "unstaged", "main4");
        await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "unstaged-feat",
            BaseCommitSha = head,
        });
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "README.md"), "changed\n");

        var result = await _inspect.ExecuteAsync(new InspectWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
        });

        Assert.True(result.IsDirty);
        Assert.Equal(1, result.UnstagedCount);
        Assert.Equal(0, result.StagedCount);
        Assert.Equal(0, result.UntrackedCount);
        Assert.Equal(0, result.ConflictCount);
    }

    [Fact]
    public async Task Missing_folder_reports_not_exists_without_throwing()
    {
        var mainPath = Path.Combine(_root, "main5");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _git.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var worktreePath = Path.Combine(_root, "features", "gone", "main5");
        await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "gone-feat",
            BaseCommitSha = head,
        });
        Directory.Delete(worktreePath, true);

        var result = await _inspect.ExecuteAsync(new InspectWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
        });

        Assert.False(result.Exists);
        Assert.Null(result.Branch);
        Assert.Null(result.HeadSha);
        Assert.Null(result.IsDirty);
    }

    [Fact]
    public async Task Locked_worktree_reports_lock_reason()
    {
        var mainPath = Path.Combine(_root, "main6");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _git.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var worktreePath = Path.Combine(_root, "features", "locked", "main6");
        await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "locked-feat",
            BaseCommitSha = head,
        });
        await RunGitAsync(mainPath, $"worktree lock --reason x \"{worktreePath}\"");

        var result = await _inspect.ExecuteAsync(new InspectWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
        });

        Assert.True(result.IsLocked);
        Assert.Equal("x", result.LockReason);
    }

    [Fact]
    public async Task No_upstream_reports_ahead_of_default()
    {
        var mainPath = Path.Combine(_root, "main7");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _git.GetHeadCommitAsync(mainPath, CancellationToken.None);
        await RunGitAsync(mainPath, "update-ref refs/remotes/origin/main main");

        var worktreePath = Path.Combine(_root, "features", "ahead", "main7");
        await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "ahead-feat",
            BaseCommitSha = head,
        });
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "a.txt"), "1\n");
        await RunGitAsync(worktreePath, "add a.txt");
        await RunGitAsync(worktreePath, "commit -m commit-a");
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "b.txt"), "2\n");
        await RunGitAsync(worktreePath, "add b.txt");
        await RunGitAsync(worktreePath, "commit -m commit-b");

        var result = await _inspect.ExecuteAsync(new InspectWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            DefaultBranch = "main",
        });

        Assert.False(result.HasUpstream);
        Assert.Null(result.AheadOfUpstream);
        Assert.Null(result.BehindUpstream);
        Assert.Equal(2, result.AheadOfDefault);
    }

    private static async Task InitGitWithCommitAsync(string repoPath)
    {
        await RunGitAsync(repoPath, "init");
        await RunGitAsync(repoPath, "config user.email test@example.com");
        await RunGitAsync(repoPath, "config user.name Test");
        // Ensure default branch is main for stable assertions across Git versions.
        await RunGitAsync(repoPath, "checkout -B main");
        await File.WriteAllTextAsync(Path.Combine(repoPath, "README.md"), "x\n");
        await RunGitAsync(repoPath, "add README.md");
        await RunGitAsync(repoPath, "commit -m init");
    }

    private static async Task RunGitAsync(string repoPath, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed: {await p.StandardError.ReadToEndAsync()}");
    }
}
