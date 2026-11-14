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
        Assert.False(first.AdoptedExisting);
        var headBefore = RunGit(RootPath, "rev-parse", "HEAD").Stdout.Trim();

        var second = await _command.ExecuteAsync(NewRequest(remote));

        Assert.True(second.Success, second.ErrorMessage);
        Assert.True(second.AdoptedExisting);
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
    public async Task A_file_already_in_the_folder_is_kept_and_reported_instead_of_stopping_the_attach()
    {
        var remote = CreateRemote(withCommit: true);
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, "README.md"), "local content that differs\n");
        File.WriteAllText(Path.Combine(RootPath, "notes.txt"), "keep me\n");

        var response = await _command.ExecuteAsync(NewRequest(remote));

        Assert.True(response.Success, response.ErrorMessage);
        Assert.False(response.AdoptedExisting);
        Assert.Equal("main", response.Branch);
        Assert.False(response.IsUnborn);
        Assert.Equal(["README.md"], response.KeptFiles);
        Assert.Equal(1, response.KeptFileCount);
        // Nothing in the folder was overwritten, and the file shows as a change against the repository's version.
        Assert.Equal("local content that differs\n", File.ReadAllText(Path.Combine(RootPath, "README.md")));
        Assert.Equal("keep me\n", File.ReadAllText(Path.Combine(RootPath, "notes.txt")));
        Assert.Equal(RunGit(remote, "rev-parse", "main").Stdout.Trim(), RunGit(RootPath, "rev-parse", "HEAD").Stdout.Trim());
        Assert.Equal("origin/main", RunGit(RootPath, "rev-parse", "--abbrev-ref", "main@{upstream}").Stdout.Trim());
        var status = RunGit(RootPath, "status", "--porcelain").Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(" M README.md", status);
        Assert.Contains("?? notes.txt", status);
        Assert.Equal(2, status.Length);
    }

    [Fact]
    public async Task Files_the_folder_does_not_have_are_written_next_to_the_kept_ones()
    {
        var remote = CreateRemote(withCommit: true);
        AddRemoteFile(remote, "docs/guide.md", "guide\n");
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, "README.md"), "local content that differs\n");

        var response = await _command.ExecuteAsync(NewRequest(remote));

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(["README.md"], response.KeptFiles);
        Assert.Equal("guide\n", File.ReadAllText(Path.Combine(RootPath, "docs", "guide.md")).Replace("\r\n", "\n"));
        Assert.Equal("", RunGit(RootPath, "ls-files", "--deleted").Stdout.Trim());
    }

    [Fact]
    public async Task An_attach_that_stopped_before_its_checkout_is_finished_as_a_fresh_attach()
    {
        var remote = CreateRemote(withCommit: true);
        Directory.CreateDirectory(RootPath);
        var readme = Path.Combine(RootPath, "README.md");
        File.WriteAllText(readme, "local content that differs\n");
        // What an older Worker left behind after a checkout collision: .git with the origin and no commit checked out.
        Assert.Equal(0, RunGit(RootPath, "init").ExitCode);
        Assert.Equal(0, RunGit(RootPath, "remote", "add", "origin", remote).ExitCode);
        Assert.NotEqual(0, RunGit(RootPath, "rev-parse", "--verify", "HEAD").ExitCode);

        var response = await _command.ExecuteAsync(NewRequest(remote));

        Assert.True(response.Success, response.ErrorMessage);
        // Not "adopted": the App still has to write graymoon.json and .gitignore.
        Assert.False(response.AdoptedExisting);
        Assert.Equal("main", response.Branch);
        Assert.False(response.IsUnborn);
        Assert.Equal(["README.md"], response.KeptFiles);
        Assert.Equal("origin/main", RunGit(RootPath, "rev-parse", "--abbrev-ref", "main@{upstream}").Stdout.Trim());
        Assert.Equal("local content that differs\n", File.ReadAllText(readme));
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
        Assert.True(second.AdoptedExisting);
        Assert.Equal("main", second.Branch);
        Assert.True(second.IsUnborn);
        Assert.NotEqual(0, RunGit(RootPath, "rev-parse", "--verify", "HEAD").ExitCode);
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

    [Fact]
    public async Task A_clone_is_checked_out_on_the_requested_remote_branch()
    {
        var remote = CreateRemote(withCommit: true);
        Assert.Equal(0, RunGit(remote, "branch", "graymoon/workspace-setup", "main").ExitCode);
        var request = NewRequest(remote);
        request.BranchName = "graymoon/workspace-setup";

        var response = await _command.ExecuteAsync(request);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("graymoon/workspace-setup", response.Branch);
        Assert.Equal("graymoon/workspace-setup", RunGit(RootPath, "rev-parse", "--abbrev-ref", "HEAD").Stdout.Trim());
    }

    [Fact]
    public async Task A_missing_requested_branch_fails_instead_of_falling_back_to_the_default()
    {
        var remote = CreateRemote(withCommit: true);
        var request = NewRequest(remote);
        request.BranchName = "deleted-branch";

        var response = await _command.ExecuteAsync(request);

        Assert.False(response.Success);
        Assert.Contains("deleted-branch", response.ErrorMessage);
        Assert.Contains("not found", response.ErrorMessage);
    }

    [Fact]
    public async Task A_non_empty_root_is_initialized_on_the_requested_branch()
    {
        var remote = CreateRemote(withCommit: true);
        Assert.Equal(0, RunGit(remote, "branch", "setup", "main").ExitCode);
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, "notes.txt"), "keep me\n");
        var request = NewRequest(remote);
        request.BranchName = "setup";

        var response = await _command.ExecuteAsync(request);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("setup", response.Branch);
    }

    [Theory]
    [InlineData("--upload-pack=x")]
    [InlineData("a b")]
    [InlineData("a..b")]
    [InlineData("x\"y")]
    public async Task An_unsafe_requested_branch_name_is_refused(string branch)
    {
        var remote = CreateRemote(withCommit: true);
        var request = NewRequest(remote);
        request.BranchName = branch;

        var response = await _command.ExecuteAsync(request);

        Assert.False(response.Success);
        Assert.Contains("not a valid branch name", response.ErrorMessage);
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
    private string CreateRemote(bool withCommit) => GitScenarios.BareRemote(_baseDir, withCommit);

    /// <summary>Commits one more file to the remote's main branch through a throwaway clone.</summary>
    private void AddRemoteFile(string remote, string relativePath, string content)
    {
        var clone = Path.Combine(_baseDir, "seed-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, RunGit(_baseDir, "clone", remote, clone).ExitCode);
        var file = Path.Combine(clone, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        Assert.Equal(0, RunGit(clone, "add", "-A").ExitCode);
        Assert.Equal(0, RunGit(clone, "-c", "user.name=test", "-c", "user.email=test@example.com", "commit", "-m", "add file").ExitCode);
        Assert.Equal(0, RunGit(clone, "push", "origin", "HEAD:main").ExitCode);
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
