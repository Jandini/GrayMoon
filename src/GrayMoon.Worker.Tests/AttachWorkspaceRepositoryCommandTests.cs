using System.Diagnostics;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>Real git against throwaway directories; git identity is passed per command so no global config is read or changed.</summary>
public sealed class AttachWorkspaceRepositoryCommandTests : IDisposable
{
    private readonly string _baseDir = Directory.CreateTempSubdirectory("graymoon-attach-test-").FullName;
    private readonly string _workspaceRoot;
    private readonly AttachWorkspaceRepositoryCommand _command;

    public AttachWorkspaceRepositoryCommandTests()
    {
        _workspaceRoot = Path.Combine(_baseDir, "workspaces");
        Directory.CreateDirectory(_workspaceRoot);

        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        var git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());
        _command = new AttachWorkspaceRepositoryCommand(git, reader);
        NewConfiguredCommand = () => new AttachWorkspaceRepositoryCommand(git, reader,
            new RepositoryConfigurationInitializer(null, NullLogger<RepositoryConfigurationInitializer>.Instance, isWindows: true));
        _configuredCommand = NewConfiguredCommand();
    }

    private readonly AttachWorkspaceRepositoryCommand _configuredCommand;

    // A new command has a new initializer: what a Worker start looks like.
    private readonly Func<AttachWorkspaceRepositoryCommand> NewConfiguredCommand;

    [Fact]
    public async Task Cloned_initialized_and_existing_roots_get_the_local_long_paths_policy()
    {
        var remote = CreateRemote(withCommit: true);

        // Empty root: cloned in place.
        var cloned = await _configuredCommand.ExecuteAsync(NewRequest(remote));
        Assert.True(cloned.Success, cloned.ErrorMessage);
        Assert.Equal("true", RunGit(RootPath, "config", "--local", "--get", "core.longpaths").Stdout.Trim());

        // Existing repository from before the policy: reconciled on the first attach after a Worker start.
        Assert.Equal(0, RunGit(RootPath, "config", "--local", "--unset-all", "core.longpaths").ExitCode);
        var again = await NewConfiguredCommand().ExecuteAsync(NewRequest(remote));
        Assert.True(again.Success, again.ErrorMessage);
        Assert.Equal("true", RunGit(RootPath, "config", "--local", "--get", "core.longpaths").Stdout.Trim());
    }

    [Fact]
    public async Task A_non_empty_root_is_configured_before_its_default_branch_checkout()
    {
        var remote = CreateRemote(withCommit: true);
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, "notes.txt"), "keep me\n");

        var response = await _configuredCommand.ExecuteAsync(NewRequest(remote));

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("true", RunGit(RootPath, "config", "--local", "--get", "core.longpaths").Stdout.Trim());
        Assert.Single(RunGit(RootPath, "config", "--local", "--get-all", "core.longpaths").Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    public void Dispose()
    {
        try
        {
            var directory = new DirectoryInfo(_baseDir);
            if (directory.Exists)
            {
                foreach (var file in directory.GetFiles("*", SearchOption.AllDirectories))
                {
                    file.Attributes = FileAttributes.Normal;
                }

                directory.Delete(true);
            }
        }
        catch
        {
            // Best-effort cleanup; a leftover temp directory is not fatal for a test run.
        }
    }

    [Fact]
    public async Task Empty_root_is_cloned_in_place()
    {
        var remote = CreateRemote(withCommit: true);

        var response = await _command.ExecuteAsync(NewRequest(remote));

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("main", response.Branch);
        Assert.False(response.IsUnborn);
        Assert.True(Directory.Exists(Path.Combine(RootPath, ".git")));
        Assert.True(File.Exists(Path.Combine(RootPath, "README.md")));
    }

    [Fact]
    public async Task Non_empty_root_without_git_is_initialized_and_checked_out()
    {
        var remote = CreateRemote(withCommit: true);
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, "notes.txt"), "keep me\n");

        var response = await _command.ExecuteAsync(NewRequest(remote));

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("main", response.Branch);
        Assert.False(response.IsUnborn);
        Assert.True(Directory.Exists(Path.Combine(RootPath, ".git")));
        Assert.Equal("keep me\n", File.ReadAllText(Path.Combine(RootPath, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(RootPath, "README.md")));

        var upstream = RunGit(RootPath, "rev-parse", "--abbrev-ref", "main@{upstream}");
        Assert.Equal(0, upstream.ExitCode);
        Assert.Equal("origin/main", upstream.Stdout.Trim());
    }

    [Fact]
    public async Task Non_empty_root_with_empty_remote_leaves_unborn_main()
    {
        var remote = CreateRemote(withCommit: false);
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, "notes.txt"), "keep me\n");

        var response = await _command.ExecuteAsync(NewRequest(remote));

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("main", response.Branch);
        Assert.True(response.IsUnborn);
        Assert.True(Directory.Exists(Path.Combine(RootPath, ".git")));
        Assert.Equal("keep me\n", File.ReadAllText(Path.Combine(RootPath, "notes.txt")));
        Assert.NotEqual(0, RunGit(RootPath, "rev-parse", "--verify", "HEAD").ExitCode);
    }

    [Fact]
    public async Task Root_with_same_origin_is_idempotent()
    {
        var remote = CreateRemote(withCommit: true);
        var first = await _command.ExecuteAsync(NewRequest(remote));
        Assert.True(first.Success, first.ErrorMessage);
        var headBefore = RunGit(RootPath, "rev-parse", "HEAD").Stdout.Trim();

        var second = await _command.ExecuteAsync(NewRequest(remote));

        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal("main", second.Branch);
        Assert.False(second.IsUnborn);
        Assert.Equal(headBefore, RunGit(RootPath, "rev-parse", "HEAD").Stdout.Trim());
    }

    [Fact]
    public async Task Root_with_different_origin_fails()
    {
        var remote = CreateRemote(withCommit: true);
        Directory.CreateDirectory(RootPath);
        Assert.Equal(0, RunGit(RootPath, "init").ExitCode);
        Assert.Equal(0, RunGit(RootPath, "remote", "add", "origin", "https://example.com/other/repo.git").ExitCode);

        var response = await _command.ExecuteAsync(NewRequest(remote));

        Assert.False(response.Success);
        Assert.Equal("Root already has a different Git repository", response.ErrorMessage);
        Assert.Equal("https://example.com/other/repo.git", RunGit(RootPath, "config", "--get", "remote.origin.url").Stdout.Trim());
    }

    [Fact]
    public async Task Checkout_collision_returns_git_message_and_keeps_dot_git()
    {
        var remote = CreateRemote(withCommit: true);
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, "README.md"), "local content that differs\n");

        var response = await _command.ExecuteAsync(NewRequest(remote));

        Assert.False(response.Success);
        Assert.False(string.IsNullOrWhiteSpace(response.ErrorMessage));
        Assert.Contains("README.md", response.ErrorMessage);
        Assert.True(Directory.Exists(Path.Combine(RootPath, ".git")));
        Assert.Equal("local content that differs\n", File.ReadAllText(Path.Combine(RootPath, "README.md")));
    }

    [Fact]
    public async Task Reattach_after_collision_checks_out_default_when_head_is_unborn()
    {
        var remote = CreateRemote(withCommit: true);
        Directory.CreateDirectory(RootPath);
        var readme = Path.Combine(RootPath, "README.md");
        File.WriteAllText(readme, "local content that differs\n");

        var first = await _command.ExecuteAsync(NewRequest(remote));
        Assert.False(first.Success);
        Assert.NotEqual(0, RunGit(RootPath, "rev-parse", "--verify", "HEAD").ExitCode);

        File.Delete(readme);
        var second = await _command.ExecuteAsync(NewRequest(remote));

        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal("main", second.Branch);
        Assert.False(second.IsUnborn);
        Assert.Equal(0, RunGit(RootPath, "rev-parse", "--verify", "HEAD").ExitCode);
        Assert.Equal("origin/main", RunGit(RootPath, "rev-parse", "--abbrev-ref", "main@{upstream}").Stdout.Trim());
        Assert.Equal("from remote\n", File.ReadAllText(readme).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Reattach_with_empty_remote_stays_unborn()
    {
        var remote = CreateRemote(withCommit: false);
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, "notes.txt"), "keep me\n");
        var first = await _command.ExecuteAsync(NewRequest(remote));
        Assert.True(first.Success, first.ErrorMessage);

        var second = await _command.ExecuteAsync(NewRequest(remote));

        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal("main", second.Branch);
        Assert.True(second.IsUnborn);
        Assert.NotEqual(0, RunGit(RootPath, "rev-parse", "--verify", "HEAD").ExitCode);
    }

    [Fact]
    public async Task Reattach_collision_again_returns_git_message()
    {
        var remote = CreateRemote(withCommit: true);
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, "README.md"), "local content that differs\n");
        Assert.False((await _command.ExecuteAsync(NewRequest(remote))).Success);

        var second = await _command.ExecuteAsync(NewRequest(remote));

        Assert.False(second.Success);
        Assert.Contains("README.md", second.ErrorMessage);
        Assert.True(Directory.Exists(Path.Combine(RootPath, ".git")));
        Assert.Equal("local content that differs\n", File.ReadAllText(Path.Combine(RootPath, "README.md")));
    }

    [Fact]
    public async Task Require_empty_root_fails_when_folder_has_files()
    {
        var remote = CreateRemote(withCommit: true);
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, "notes.txt"), "keep me\n");
        var request = NewRequest(remote);
        request.RequireEmptyRoot = true;

        var response = await _command.ExecuteAsync(request);

        Assert.False(response.Success);
        Assert.Equal("The folder already exists and is not empty.", response.ErrorMessage);
        Assert.False(Directory.Exists(Path.Combine(RootPath, ".git")));
        Assert.Single(Directory.GetFileSystemEntries(RootPath));
        Assert.Equal("keep me\n", File.ReadAllText(Path.Combine(RootPath, "notes.txt")));
    }

    [Fact]
    public async Task Require_empty_root_allows_missing_or_empty_folder()
    {
        var remote = CreateRemote(withCommit: true);
        var request = NewRequest(remote);
        request.RequireEmptyRoot = true;

        var missing = await _command.ExecuteAsync(request);
        Assert.True(missing.Success, missing.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(RootPath, "README.md")));

        request.WorkspaceName = "ws-empty";
        var emptyRoot = Path.Combine(_workspaceRoot, "ws-empty");
        Directory.CreateDirectory(emptyRoot);
        var empty = await _command.ExecuteAsync(request);
        Assert.True(empty.Success, empty.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(emptyRoot, "README.md")));
    }

    [Fact]
    public async Task Reattach_with_require_empty_root_false_is_unchanged()
    {
        var remote = CreateRemote(withCommit: true);
        Assert.True((await _command.ExecuteAsync(NewRequest(remote))).Success);
        var request = NewRequest(remote);
        request.RequireEmptyRoot = false;

        var second = await _command.ExecuteAsync(request);

        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal("main", second.Branch);
        Assert.False(second.IsUnborn);
    }

    private string RootPath => Path.Combine(_workspaceRoot, "ws");

    private AttachWorkspaceRepositoryRequest NewRequest(string cloneUrl) => new()
    {
        WorkspaceRoot = _workspaceRoot,
        WorkspaceName = "ws",
        CloneUrl = cloneUrl,
        WorkspaceId = 1,
        RepositoryId = 2,
    };

    /// <summary>Creates a bare repository (HEAD on main) and, optionally, pushes one commit with README.md to it.</summary>
    private string CreateRemote(bool withCommit)
    {
        var bare = Path.Combine(_baseDir, "remote.git");
        Directory.CreateDirectory(bare);
        Assert.Equal(0, RunGit(bare, "init", "--bare", "--initial-branch=main").ExitCode);

        if (withCommit)
        {
            var source = Path.Combine(_baseDir, "source");
            Directory.CreateDirectory(source);
            Assert.Equal(0, RunGit(source, "init", "--initial-branch=main").ExitCode);
            File.WriteAllText(Path.Combine(source, "README.md"), "from remote\n");
            Assert.Equal(0, RunGit(source, "add", "--all").ExitCode);
            Assert.Equal(0, RunGit(source,
                "-c", "user.name=GrayMoon Test", "-c", "user.email=graymoon-test@example.com", "-c", "commit.gpgsign=false",
                "commit", "-m", "Initial commit").ExitCode);
            Assert.Equal(0, RunGit(source, "push", bare, "main").ExitCode);
        }

        return bare;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git process.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout, stderr);
    }
}
