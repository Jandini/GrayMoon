using System.Text;
using GrayMoon.Worker.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

public sealed class GitServiceStageAndCommitTests : IDisposable
{
    private readonly TempGitRepositoryFixture _repo = new();
    private readonly RecordingCommandLineService _recorder;
    private readonly GitService _git;
    private GitCliRepositoryReader _reader = null!;

    public GitServiceStageAndCommitTests()
    {
        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        _recorder = new RecordingCommandLineService(inner);
        var runner = new GitProcessRunner(_recorder, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader, new LibGit2SharpGitIgnoreService());
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task SkipHooks_passes_empty_hooks_path_on_add_and_commit()
    {
        _repo.CommitInitial();
        _repo.WriteFile("src/Lib/Lib.csproj", "<Project />\n");

        var (success, committed, error) = await _git.StageAndCommitAsync(
            _repo.RepositoryPath,
            ["src/Lib/Lib.csproj"],
            "chore(deps): update package versions",
            CancellationToken.None,
            skipHooks: true);

        Assert.True(success);
        Assert.True(committed);
        Assert.Null(error);

        var add = Assert.Single(_recorder.ArgumentListCalls, c => c.Contains("add"));
        AssertHooksPrefix(add);
        Assert.Contains("--pathspec-from-file=-", add);
        Assert.Contains("--pathspec-file-nul", add);

        var commit = Assert.Single(_recorder.ArgumentListCalls, c => c.Contains("commit"));
        AssertHooksPrefix(commit);
        Assert.Contains("-F", commit);
        Assert.Contains("-", commit);
        Assert.DoesNotContain(_recorder.ArgumentListCalls, c => c.Contains("check-ignore"));
        Assert.DoesNotContain(_recorder.ArgumentListCalls, c => c.Contains("-f"));
    }

    [Fact]
    public async Task Without_skipHooks_does_not_set_hooks_path()
    {
        _repo.CommitInitial();
        _repo.WriteFile("src/Lib/Lib.csproj", "<Project />\n");

        var (success, committed, error) = await _git.StageAndCommitAsync(
            _repo.RepositoryPath,
            ["src/Lib/Lib.csproj"],
            "chore(deps): update package versions",
            CancellationToken.None);

        Assert.True(success);
        Assert.True(committed);
        Assert.Null(error);

        var add = Assert.Single(_recorder.ArgumentListCalls, c => c.Contains("add"));
        Assert.DoesNotContain(add, a => a.StartsWith("core.hooksPath=", StringComparison.Ordinal));
        Assert.DoesNotContain("-c", add);
    }

    [Fact]
    public async Task Stages_via_pathspec_from_file_including_paths_with_spaces()
    {
        _repo.CommitInitial();
        _repo.WriteFile("src/My Project/App.csproj", "<Project />\n");

        var (success, committed, error) = await _git.StageAndCommitAsync(
            _repo.RepositoryPath,
            ["src/My Project/App.csproj"],
            "chore: add csproj",
            CancellationToken.None);

        Assert.True(success);
        Assert.True(committed);
        Assert.Null(error);

        var add = Assert.Single(_recorder.ArgumentListCalls, c => c.Contains("add"));
        Assert.Contains("--pathspec-from-file=-", add);
        Assert.Contains("--pathspec-file-nul", add);
        Assert.DoesNotContain("src/My Project/App.csproj", add);

        var stdin = Assert.Single(_recorder.Calls, c => c.ArgumentList?.Contains("add") == true).StdinBytes;
        Assert.NotNull(stdin);
        var decoded = Encoding.UTF8.GetString(stdin!);
        Assert.Contains("src/My Project/App.csproj", decoded);

        var names = _repo.RunGit("log", "-1", "--name-only", "--pretty=format:").Stdout;
        Assert.Contains("src/My Project/App.csproj", names);
    }

    [Fact]
    public async Task Nothing_staged_returns_success_without_committed()
    {
        _repo.CommitInitial("file.txt", "same\n");

        var (success, committed, error) = await _git.StageAndCommitAsync(
            _repo.RepositoryPath,
            ["file.txt"],
            "chore: no change",
            CancellationToken.None);

        Assert.True(success);
        Assert.False(committed);
        Assert.Null(error);
    }

    [Fact]
    public async Task Ignored_paths_are_dropped_before_a_single_add_and_the_rest_is_committed()
    {
        _repo.CommitInitial();
        _repo.WriteFile(".gitignore", ".work\n");
        _repo.RunGit("add", ".gitignore");
        _repo.RunGit("commit", "-m", "ignore .work");
        _repo.WriteFile("src/Lib/Lib.csproj", "<Project />\n");
        _repo.WriteFile(".work/Ignored.csproj", "<Project />\n");

        var (success, committed, error) = await _git.StageAndCommitAsync(
            _repo.RepositoryPath,
            ["src/Lib/Lib.csproj", ".work/Ignored.csproj"],
            "chore(deps): update package versions",
            CancellationToken.None,
            skipHooks: true);

        Assert.True(success);
        Assert.True(committed);
        Assert.Null(error);

        Assert.DoesNotContain(_recorder.ArgumentListCalls, c => c.Contains("check-ignore"));
        Assert.Equal(1, _recorder.ArgumentListCalls.Count(c => c.Contains("add")));
        Assert.Contains(_recorder.ArgumentListCalls, c => c.Contains("--literal-pathspecs"));
        Assert.DoesNotContain(_recorder.ArgumentListCalls, c => c.Contains("-f"));

        var names = _repo.RunGit("log", "-1", "--name-only", "--pretty=format:").Stdout;
        Assert.Contains("src/Lib/Lib.csproj", names);
        Assert.DoesNotContain("Ignored.csproj", names);

        Assert.True(File.Exists(Path.Combine(_repo.RepositoryPath, ".work", "Ignored.csproj")));
        var trackedWork = _repo.RunGit("ls-files", ".work").Stdout;
        Assert.True(string.IsNullOrWhiteSpace(trackedWork));
    }

    [Fact]
    public async Task All_ignored_paths_is_nothing_staged_and_runs_no_add()
    {
        _repo.CommitInitial();
        _repo.WriteFile(".gitignore", ".work\n");
        _repo.RunGit("add", ".gitignore");
        _repo.RunGit("commit", "-m", "ignore .work");
        _repo.WriteFile(".work/Ignored.csproj", "<Project />\n");

        var (success, committed, error) = await _git.StageAndCommitAsync(
            _repo.RepositoryPath,
            [".work/Ignored.csproj"],
            "chore(deps): update package versions",
            CancellationToken.None);

        Assert.True(success);
        Assert.False(committed);
        Assert.Null(error);
        Assert.DoesNotContain(_recorder.ArgumentListCalls, c => c.Contains("check-ignore"));
        Assert.DoesNotContain(_recorder.ArgumentListCalls, c => c.Contains("add"));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("/outside.txt")]
    public async Task Invalid_paths_are_rejected_before_any_git_call(string path)
    {
        _repo.CommitInitial();

        var (success, committed, error) = await _git.StageAndCommitAsync(
            _repo.RepositoryPath, [path], "msg", CancellationToken.None);

        Assert.False(success);
        Assert.False(committed);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain(_recorder.ArgumentListCalls, c => c.Contains("add"));
    }

    [Fact]
    public async Task Deleted_tracked_file_matching_an_ignore_rule_is_staged_and_committed()
    {
        _repo.CommitInitial("tracked.csproj", "<Project />\n");
        _repo.WriteFile(".gitignore", "tracked.csproj\n");
        _repo.RunGit("add", ".gitignore");
        _repo.RunGit("commit", "-m", "ignore tracked csproj");
        _repo.DeleteFile("tracked.csproj");

        var (success, committed, error) = await _git.StageAndCommitAsync(
            _repo.RepositoryPath, ["tracked.csproj"], "chore: remove", CancellationToken.None);

        Assert.True(success, error);
        Assert.True(committed);
        Assert.True(string.IsNullOrWhiteSpace(_repo.RunGit("ls-files", "tracked.csproj").Stdout));
    }

    [Fact]
    public async Task Path_with_glob_characters_is_staged_literally()
    {
        _repo.CommitInitial();
        _repo.WriteFile("[x].cs", "a\n");
        _repo.WriteFile("x.cs", "b\n");

        var (success, committed, error) = await _git.StageAndCommitAsync(
            _repo.RepositoryPath, ["[x].cs"], "chore: literal", CancellationToken.None);

        Assert.True(success, error);
        Assert.True(committed);
        Assert.Equal("[x].cs", _repo.RunGit("ls-files", "*.cs").Stdout.Trim());
    }

    [Fact]
    public async Task Tracked_then_gitignored_file_still_stages_without_check_ignore()
    {
        _repo.CommitInitial("tracked.csproj", "<Project />\n");
        _repo.WriteFile(".gitignore", "tracked.csproj\n");
        _repo.RunGit("add", ".gitignore");
        _repo.RunGit("commit", "-m", "ignore tracked csproj");
        _repo.WriteFile("tracked.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");

        var (success, committed, error) = await _git.StageAndCommitAsync(
            _repo.RepositoryPath,
            ["tracked.csproj"],
            "chore: update tracked csproj",
            CancellationToken.None);

        Assert.True(success);
        Assert.True(committed);
        Assert.Null(error);
        Assert.DoesNotContain(_recorder.ArgumentListCalls, c => c.Contains("check-ignore"));
    }

    private static void AssertHooksPrefix(IReadOnlyList<string> args)
    {
        Assert.Equal("-c", args[0]);
        Assert.StartsWith("core.hooksPath=", args[1], StringComparison.Ordinal);
        Assert.Contains("GrayMoon-empty-hooks", args[1], StringComparison.Ordinal);
    }

    private sealed class RecordingCommandLineService(ICommandLineService inner) : ICommandLineService
    {
        public List<RecordedCall> Calls { get; } = [];

        public IEnumerable<IReadOnlyList<string>> ArgumentListCalls =>
            Calls.Where(c => c.ArgumentList != null).Select(c => c.ArgumentList!);

        public Task<CommandLineResult> RunAsync(
            string fileName,
            string arguments,
            string? workingDirectory = null,
            string? stdin = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null)
        {
            Calls.Add(new RecordedCall(fileName, arguments, null, stdin == null ? null : Encoding.UTF8.GetBytes(stdin)));
            return inner.RunAsync(fileName, arguments, workingDirectory, stdin, cancellationToken, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout);
        }

        public Task<CommandLineResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            byte[]? stdinBytes = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null)
        {
            Calls.Add(new RecordedCall(fileName, null, [.. arguments], stdinBytes));
            return inner.RunAsync(fileName, arguments, workingDirectory, stdinBytes, cancellationToken, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout);
        }

        public sealed record RecordedCall(string FileName, string? Arguments, IReadOnlyList<string>? ArgumentList, byte[]? StdinBytes);
    }
}
