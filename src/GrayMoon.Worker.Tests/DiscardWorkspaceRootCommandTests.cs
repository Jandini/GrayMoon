using System.Diagnostics;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Restore rollback of the Workspace root, with real git against throwaway directories: only an empty folder or a
/// clean clone of the restored repository is ever deleted.
/// </summary>
public sealed class DiscardWorkspaceRootCommandTests : IDisposable
{
    private readonly string _baseDir = Directory.CreateTempSubdirectory("graymoon-discard-test-").FullName;
    private readonly string _workspaceRoot;
    private readonly AttachWorkspaceRepositoryCommand _attach;
    private readonly DiscardWorkspaceRootCommand _discard;
    private readonly GetWorkspaceExistsCommand _exists = new();

    public DiscardWorkspaceRootCommandTests()
    {
        _workspaceRoot = Path.Combine(_baseDir, "workspaces");
        Directory.CreateDirectory(_workspaceRoot);

        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        var git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());
        _attach = new AttachWorkspaceRepositoryCommand(git, reader);
        _discard = new DiscardWorkspaceRootCommand(runner, reader);
    }

    public void Dispose()
    {
        try
        {
            var directory = new DirectoryInfo(_baseDir);
            if (directory.Exists)
            {
                foreach (var file in directory.GetFiles("*", SearchOption.AllDirectories))
                    file.Attributes = FileAttributes.Normal;
                directory.Delete(true);
            }
        }
        catch
        {
            // Best-effort cleanup; a leftover temp directory is not fatal for a test run.
        }
    }

    [Fact]
    public async Task A_clean_clone_of_the_restored_repository_is_deleted()
    {
        var remote = await CloneIntoRootAsync();

        var response = await _discard.ExecuteAsync(Request(remote));

        Assert.True(response.Removed, response.Reason);
        Assert.False(Directory.Exists(RootPath));
    }

    [Fact]
    public async Task A_folder_that_existed_before_is_emptied_but_kept()
    {
        var remote = await CloneIntoRootAsync();

        var response = await _discard.ExecuteAsync(Request(remote, keepFolder: true));

        Assert.True(response.Removed, response.Reason);
        Assert.True(Directory.Exists(RootPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(RootPath));
    }

    [Fact]
    public async Task An_empty_folder_is_deleted_and_a_missing_one_is_fine()
    {
        Directory.CreateDirectory(RootPath);

        var removed = await _discard.ExecuteAsync(Request("https://example.invalid/acme/ws.git"));
        Assert.True(removed.Removed);
        Assert.False(Directory.Exists(RootPath));

        var missing = await _discard.ExecuteAsync(Request("https://example.invalid/acme/ws.git"));
        Assert.True(missing.Removed);
    }

    [Fact]
    public async Task Local_changes_keep_the_folder()
    {
        var remote = await CloneIntoRootAsync();
        File.WriteAllText(Path.Combine(RootPath, "notes.txt"), "mine\n");

        var response = await _discard.ExecuteAsync(Request(remote));

        Assert.False(response.Removed);
        Assert.Equal(DiscardWorkspaceRootCommand.LocalChangesReason, response.Reason);
        Assert.True(File.Exists(Path.Combine(RootPath, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(RootPath, "README.md")));
    }

    [Fact]
    public async Task A_different_repository_keeps_the_folder()
    {
        await CloneIntoRootAsync();

        var response = await _discard.ExecuteAsync(Request("https://example.invalid/someone/else.git"));

        Assert.False(response.Removed);
        Assert.Equal(DiscardWorkspaceRootCommand.DifferentRepositoryReason, response.Reason);
        Assert.True(File.Exists(Path.Combine(RootPath, "README.md")));
    }

    [Fact]
    public async Task Files_without_git_keep_the_folder()
    {
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, "notes.txt"), "mine\n");

        var response = await _discard.ExecuteAsync(Request("https://example.invalid/acme/ws.git"));

        Assert.False(response.Removed);
        Assert.Equal(DiscardWorkspaceRootCommand.ForeignFilesReason, response.Reason);
        Assert.True(File.Exists(Path.Combine(RootPath, "notes.txt")));
    }

    [Fact]
    public async Task Workspace_exists_reports_whether_the_folder_is_empty()
    {
        var missing = await _exists.ExecuteAsync(new GetWorkspaceExistsRequest { WorkspaceRoot = _workspaceRoot, WorkspaceName = "ws" });
        Assert.False(missing.Exists);
        Assert.Null(missing.IsEmpty);

        Directory.CreateDirectory(RootPath);
        var empty = await _exists.ExecuteAsync(new GetWorkspaceExistsRequest { WorkspaceRoot = _workspaceRoot, WorkspaceName = "ws" });
        Assert.True(empty.Exists);
        Assert.True(empty.IsEmpty);

        Directory.CreateDirectory(Path.Combine(RootPath, "sub"));
        var occupied = await _exists.ExecuteAsync(new GetWorkspaceExistsRequest { WorkspaceRoot = _workspaceRoot, WorkspaceName = "ws" });
        Assert.True(occupied.Exists);
        Assert.False(occupied.IsEmpty);
    }

    private string RootPath => Path.Combine(_workspaceRoot, "ws");

    private DiscardWorkspaceRootRequest Request(string cloneUrl, bool keepFolder = false) => new()
    {
        WorkspaceRoot = _workspaceRoot,
        WorkspaceName = "ws",
        CloneUrl = cloneUrl,
        KeepFolder = keepFolder,
    };

    /// <summary>Creates a remote with one commit and attaches it to an empty root exactly like a restore does.</summary>
    private async Task<string> CloneIntoRootAsync()
    {
        var bare = Path.Combine(_baseDir, "remote.git");
        Directory.CreateDirectory(bare);
        Assert.Equal(0, RunGit(bare, "init", "--bare", "--initial-branch=main"));

        var source = Path.Combine(_baseDir, "source");
        Directory.CreateDirectory(source);
        Assert.Equal(0, RunGit(source, "init", "--initial-branch=main"));
        File.WriteAllText(Path.Combine(source, "README.md"), "from remote\n");
        Assert.Equal(0, RunGit(source, "add", "--all"));
        Assert.Equal(0, RunGit(source,
            "-c", "user.name=GrayMoon Test", "-c", "user.email=graymoon-test@example.com", "-c", "commit.gpgsign=false",
            "commit", "-m", "Initial commit"));
        Assert.Equal(0, RunGit(source, "push", bare, "main"));

        var attached = await _attach.ExecuteAsync(new AttachWorkspaceRepositoryRequest
        {
            WorkspaceRoot = _workspaceRoot,
            WorkspaceName = "ws",
            CloneUrl = bare,
            WorkspaceId = 1,
            RepositoryId = 2,
            RequireEmptyRoot = true,
        });
        Assert.True(attached.Success, attached.ErrorMessage);
        return bare;
    }

    private static int RunGit(string workingDirectory, params string[] args)
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
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git process.");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
