using GrayMoon.Agent.Commands;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Tests;

public sealed class GitWorktreeCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-wt-").FullName;
    private readonly GitService _git;
    private readonly ListGitWorktreesCommand _list;
    private readonly CreateGitWorktreeCommand _create;
    private readonly RemoveGitWorktreeCommand _remove;

    public GitWorktreeCommandTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _git = new GitService(Options.Create(new AgentOptions()), NullLogger<GitService>.Instance, runner);
        _list = new ListGitWorktreesCommand(_git);
        _create = new CreateGitWorktreeCommand(_git);
        _remove = new RemoveGitWorktreeCommand(_git);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task List_create_idempotent_create_and_remove_roundtrip()
    {
        var mainPath = Path.Combine(_root, "main");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _git.GetHeadCommitAsync(mainPath, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(head));

        var listed = await _list.ExecuteAsync(new ListGitWorktreesRequest { MainRepositoryPath = mainPath });
        Assert.True(listed.Success);
        Assert.NotNull(listed.Worktrees);
        Assert.Single(listed.Worktrees!);
        Assert.False(string.IsNullOrWhiteSpace(listed.Worktrees![0].BranchName));
        Assert.Equal(await CurrentBranchAsync(mainPath), listed.Worktrees![0].BranchName);

        var worktreePath = Path.Combine(_root, "features", "ABC-1", "main");
        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "ABC-1",
            BaseCommitSha = head,
        });
        Assert.True(created.Success, created.ErrorMessage);
        Assert.False(created.AlreadyExisted);
        Assert.Equal("ABC-1", created.BranchName);
        Assert.True(Directory.Exists(created.WorktreePath!));
        Assert.Equal(head, created.HeadSha, StringComparer.OrdinalIgnoreCase);

        var again = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "ABC-1",
            BaseCommitSha = head,
        });
        Assert.True(again.Success, again.ErrorMessage);
        Assert.True(again.AlreadyExisted);

        var occupancy = GitWorktreeOccupancy.ClassifyBranch(
            (await _list.ExecuteAsync(new ListGitWorktreesRequest { MainRepositoryPath = mainPath })).Worktrees,
            "ABC-1",
            mainPath);
        Assert.Equal(GitWorktreeBranchOccupancyKind.OccupiedElsewhere, occupancy);

        var removed = await _remove.ExecuteAsync(new RemoveGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            Force = false,
        });
        Assert.True(removed.Success, removed.ErrorMessage);
        Assert.False(removed.AlreadyRemoved);

        var listedAfter = await _list.ExecuteAsync(new ListGitWorktreesRequest { MainRepositoryPath = mainPath });
        Assert.True(listedAfter.Success);
        Assert.Single(listedAfter.Worktrees!);
        Assert.Null(GitWorktreeOccupancy.FindByBranch(listedAfter.Worktrees, "ABC-1"));

        var removeAgain = await _remove.ExecuteAsync(new RemoveGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
        });
        Assert.True(removeAgain.Success);
        Assert.True(removeAgain.AlreadyRemoved);
    }

    [Fact]
    public async Task Create_rejects_branch_already_checked_out_elsewhere()
    {
        var mainPath = Path.Combine(_root, "main2");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _git.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var firstPath = Path.Combine(_root, "features", "feat-a", "main2");
        var first = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = firstPath,
            BranchName = "feat-a",
            BaseCommitSha = head,
        });
        Assert.True(first.Success, first.ErrorMessage);

        var second = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = Path.Combine(_root, "features", "other", "main2"),
            BranchName = "feat-a",
            BaseCommitSha = head,
        });
        Assert.False(second.Success);
        Assert.Equal("BranchOccupied", second.ErrorCode);
    }

    [Fact]
    public async Task Remove_without_force_fails_when_dirty()
    {
        var mainPath = Path.Combine(_root, "main3");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _git.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var worktreePath = Path.Combine(_root, "features", "dirty", "main3");
        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "dirty-feat",
            BaseCommitSha = head,
        });
        Assert.True(created.Success, created.ErrorMessage);

        await File.WriteAllTextAsync(Path.Combine(worktreePath, "dirty.txt"), "x\n");

        var cleanRemove = await _remove.ExecuteAsync(new RemoveGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            Force = false,
        });
        Assert.False(cleanRemove.Success);
        Assert.Equal("GitFailed", cleanRemove.ErrorCode);

        var forceRemove = await _remove.ExecuteAsync(new RemoveGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            Force = true,
        });
        Assert.True(forceRemove.Success, forceRemove.ErrorMessage);
    }

    [Fact]
    public async Task Remove_refuses_primary_worktree()
    {
        var mainPath = Path.Combine(_root, "main4");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);

        var result = await _remove.ExecuteAsync(new RemoveGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = mainPath,
            Force = true,
        });
        Assert.False(result.Success);
        Assert.Equal("CannotRemovePrimary", result.ErrorCode);
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

    private static async Task<string> CurrentBranchAsync(string repoPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = "branch --show-current",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout.Trim();
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
