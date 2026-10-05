using GrayMoon.Agent.Commands;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Tests;

/// <summary>
/// Feature worktrees live under <c>features\&lt;name&gt;\&lt;repo&gt;</c>, deeper than a Workspace checkout, so on
/// Windows the Agent sets <c>core.longpaths=true</c> in the repository's own config before it creates or
/// removes a worktree. Real git, same pattern as <see cref="RemoveWorktreeResidueTests"/>.
/// </summary>
public sealed class GitServiceLongPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-lp-").FullName;
    private readonly GitService _git;
    private readonly CreateGitWorktreeCommand _create;
    private readonly RemoveGitWorktreeCommand _remove;

    public GitServiceLongPathsTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _git = new GitService(Options.Create(new AgentOptions()), NullLogger<GitService>.Instance, runner);
        _create = new CreateGitWorktreeCommand(_git);
        _remove = new RemoveGitWorktreeCommand(_git);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Create_sets_core_longpaths_in_the_repository_config_on_Windows_only()
    {
        var main = await CreateRepoAsync("main1");
        var head = await _git.GetHeadCommitAsync(main, CancellationToken.None);
        Assert.Null(await GetLocalLongPathsAsync(main));

        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = main,
            WorktreePath = Path.Combine(_root, "features", "lp-feat", "main1"),
            BranchName = "lp-feat",
            BaseCommitSha = head,
        });

        Assert.True(created.Success, created.ErrorMessage);
        var value = await GetLocalLongPathsAsync(main);
        if (OperatingSystem.IsWindows())
            Assert.Equal("true", value);
        else
            Assert.Null(value); // not a Windows setting; other OSes are left untouched
    }

    [Fact]
    public async Task Create_with_an_existing_local_true_does_not_add_a_second_entry()
    {
        var main = await CreateRepoAsync("main2");
        await RunGitAsync(main, "config --local core.longpaths true");
        var head = await _git.GetHeadCommitAsync(main, CancellationToken.None);

        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = main,
            WorktreePath = Path.Combine(_root, "features", "lp-same", "main2"),
            BranchName = "lp-same",
            BaseCommitSha = head,
        });

        Assert.True(created.Success, created.ErrorMessage);
        Assert.Equal("true", await GetLocalLongPathsAsync(main));
        Assert.Single((await RunGitOutputAsync(main, "config --local --get-all core.longpaths"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Create_and_remove_a_worktree_whose_files_exceed_260_characters_on_Windows()
    {
        if (!OperatingSystem.IsWindows())
            return; // The 260-character limit is a Windows limit.

        var main = await CreateRepoAsync("main3");
        var longRelative = string.Join('/', Enumerable.Range(0, 6).Select(i => $"deep-folder-{i}-" + new string('x', 40)));
        longRelative += "/file.txt";
        var head = await CommitLongPathFileAsync(main, longRelative);

        var worktreePath = Path.Combine(_root, "features", "lp-deep", "main3");
        var storageRoot = Path.Combine(_root, "features");
        var featureRoot = Path.Combine(storageRoot, "lp-deep");
        Assert.True(worktreePath.Length + longRelative.Length > 260, "The test file path must exceed MAX_PATH.");

        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = main,
            WorktreePath = worktreePath,
            BranchName = "lp-deep",
            BaseCommitSha = head,
        });
        Assert.True(created.Success, created.ErrorMessage);

        var longFile = @"\\?\" + Path.Combine(worktreePath, longRelative.Replace('/', '\\'));
        Assert.True(File.Exists(longFile), "The deep file should be checked out in the Feature worktree.");

        var removed = await _remove.ExecuteAsync(new RemoveGitWorktreeRequest
        {
            MainRepositoryPath = main,
            WorktreePath = worktreePath,
            Force = true,
            FeatureRootPath = featureRoot,
            FeatureStorageRoot = storageRoot,
        });
        Assert.True(removed.Success, removed.ErrorMessage);
        Assert.False(removed.ResidueRemaining);
        Assert.False(Directory.Exists(worktreePath));
    }

    [Fact]
    public async Task Remove_sets_core_longpaths_for_a_Feature_created_before_the_setting_existed()
    {
        var main = await CreateRepoAsync("main4");
        var head = await _git.GetHeadCommitAsync(main, CancellationToken.None);
        var worktreePath = Path.Combine(_root, "features", "lp-old", "main4");
        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = main,
            WorktreePath = worktreePath,
            BranchName = "lp-old",
            BaseCommitSha = head,
        });
        Assert.True(created.Success, created.ErrorMessage);

        // Simulate a Feature made by an older build: no local setting.
        await RunGitAsync(main, "config --local --unset-all core.longpaths", allowFailure: true);
        Assert.Null(await GetLocalLongPathsAsync(main));

        var removed = await _remove.ExecuteAsync(new RemoveGitWorktreeRequest
        {
            MainRepositoryPath = main,
            WorktreePath = worktreePath,
            Force = true,
        });

        Assert.True(removed.Success, removed.ErrorMessage);
        var value = await GetLocalLongPathsAsync(main);
        if (OperatingSystem.IsWindows())
            Assert.Equal("true", value);
        else
            Assert.Null(value);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private async Task<string> CreateRepoAsync(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        await RunGitAsync(path, "init");
        await RunGitAsync(path, "config user.email test@example.com");
        await RunGitAsync(path, "config user.name Test");
        await RunGitAsync(path, "checkout -B main");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "x\n");
        await RunGitAsync(path, "add README.md");
        await RunGitAsync(path, "commit -m init");
        return path;
    }

    /// <summary>
    /// Commits a file at a very long path without ever touching the working tree (hash-object plus
    /// update-index --cacheinfo), so building the fixture does not itself need long-path support.
    /// </summary>
    private async Task<string> CommitLongPathFileAsync(string repoPath, string relativePath)
    {
        var blobSource = Path.Combine(_root, "blob.txt");
        await File.WriteAllTextAsync(blobSource, "deep\n");
        var sha = (await RunGitOutputAsync(repoPath, $"hash-object -w \"{blobSource}\"")).Trim();
        await RunGitAsync(repoPath, $"update-index --add --cacheinfo 100644,{sha},{relativePath}");
        await RunGitAsync(repoPath, "commit -m deep");
        return (await _git.GetHeadCommitAsync(repoPath, CancellationToken.None))!;
    }

    private async Task<string?> GetLocalLongPathsAsync(string repoPath)
    {
        var output = (await RunGitOutputAsync(repoPath, "config --local --get core.longpaths", allowFailure: true)).Trim();
        return output.Length == 0 ? null : output;
    }

    private static Task RunGitAsync(string repoPath, string args, bool allowFailure = false)
        => RunGitOutputAsync(repoPath, args, allowFailure);

    private static async Task<string> RunGitOutputAsync(string repoPath, string args, bool allowFailure = false)
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
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (p.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException($"git {args} failed: {stderr}");
        return stdout;
    }
}
