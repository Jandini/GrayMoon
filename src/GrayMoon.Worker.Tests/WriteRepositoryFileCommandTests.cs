using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

public sealed class WriteRepositoryFileCommandTests : IDisposable
{
    private const string RepositoryName = "ws-repo";

    private readonly string _workspaceRoot = Directory.CreateTempSubdirectory("graymoon-write-file-test-").FullName;
    private readonly WriteRepositoryFileCommand _command;

    public WriteRepositoryFileCommandTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        var git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());
        _command = new WriteRepositoryFileCommand();

        // The repository is the Workspace repository, so its working tree is the workspace folder itself.
        Directory.CreateDirectory(RootPath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspaceRoot, true);
        }
        catch
        {
            // Best-effort cleanup; a leftover temp directory is not fatal for a test run.
        }
    }

    [Fact]
    public async Task Writes_utf8_without_bom()
    {
        var response = await _command.ExecuteAsync(NewRequest(".graymoon.json", "{\"name\":\"café\"}\n"));

        Assert.True(response.Success, response.ErrorMessage);
        Assert.True(response.Written);
        var bytes = File.ReadAllBytes(Path.Combine(RootPath, ".graymoon.json"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("{\"name\":\"café\"}\n"), bytes);
        Assert.Empty(Directory.GetFiles(RootPath, "*.graymoon-tmp"));
    }

    [Fact]
    public async Task Skips_when_identical()
    {
        var first = await _command.ExecuteAsync(NewRequest("file.txt", "same\n"));
        Assert.True(first.Written);
        var path = Path.Combine(RootPath, "file.txt");
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var second = await _command.ExecuteAsync(NewRequest("file.txt", "same\n"));

        Assert.True(second.Success, second.ErrorMessage);
        Assert.False(second.Written);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));

        var forced = await _command.ExecuteAsync(NewRequest("file.txt", "same\n", onlyIfChanged: false));
        Assert.True(forced.Success, forced.ErrorMessage);
        Assert.True(forced.Written);
    }

    [Fact]
    public async Task Rejects_parent_traversal()
    {
        var response = await _command.ExecuteAsync(NewRequest("../escape.txt", "x"));

        Assert.False(response.Success);
        Assert.False(string.IsNullOrWhiteSpace(response.ErrorMessage));
        Assert.False(File.Exists(Path.Combine(_workspaceRoot, "escape.txt")));
    }

    [Fact]
    public async Task Rejects_dot_git_path()
    {
        var response = await _command.ExecuteAsync(NewRequest(".git/hooks/post-commit", "x"));

        Assert.False(response.Success);
        Assert.False(string.IsNullOrWhiteSpace(response.ErrorMessage));
        Assert.False(Directory.Exists(Path.Combine(RootPath, ".git")));
    }

    [Fact]
    public async Task Creates_missing_subdirectory()
    {
        var response = await _command.ExecuteAsync(NewRequest("docs/nested/readme.md", "# hi\n"));

        Assert.True(response.Success, response.ErrorMessage);
        Assert.True(response.Written);
        Assert.Equal("# hi\n", File.ReadAllText(Path.Combine(RootPath, "docs", "nested", "readme.md")));
    }

    private string RootPath => Path.Combine(_workspaceRoot, "ws");

    private WriteRepositoryFileRequest NewRequest(string filePath, string content, bool onlyIfChanged = true) => new()
    {
        WorkspaceRoot = _workspaceRoot,
        WorkspaceName = "ws",
        RepositoryName = RepositoryName,
        WorkspaceRepositoryName = RepositoryName,
        FilePath = filePath,
        Content = content,
        OnlyIfChanged = onlyIfChanged,
    };
}
