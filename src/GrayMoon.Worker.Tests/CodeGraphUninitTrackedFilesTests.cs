using GrayMoon.Common;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Removing a Feature's CodeGraph index must leave the checkout as Git has it. <c>codegraph uninit</c> deletes the whole
/// <c>.codegraph</c> folder, and a Workspace repository can have <c>.codegraph/.gitignore</c> committed: the deleted
/// file made the Feature's Workspace repository dirty between the Remove Feature check and the removal, which then
/// failed with "changed since the check" (logs of 2026-11-24, 23:13:13).
/// </summary>
public sealed class CodeGraphUninitTrackedFilesTests : IDisposable
{
    private const string Executable = @"C:\tools\codegraph.cmd";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graymoon-codegraph-tracked-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;

    public CodeGraphUninitTrackedFilesTests()
    {
        _repo = Path.Combine(_root, "repo");
        GitScenarios.CommittedRepository(_repo);
    }

    public void Dispose() => TestDirectory.Delete(_root);

    private string IndexFolder => Path.Combine(_repo, CodeGraphCli.DataFolderName);

    private string IgnoreFile => Path.Combine(IndexFolder, ".gitignore");

    [Fact]
    public async Task Uninit_leaves_a_committed_gitignore_in_place_and_the_repository_clean()
    {
        CommitIgnoreFile();
        CreateIndex();

        var response = await new UninitCodeGraphCommand(CreateCli(Executable))
            .ExecuteAsync(new UninitCodeGraphRequest { Root = _repo });

        Assert.Equal("Removed", response.Outcome);
        Assert.Null(response.Message);
        Assert.True(File.Exists(IgnoreFile));
        Assert.False(File.Exists(Path.Combine(IndexFolder, CodeGraphCli.DatabaseFileName)));
        Assert.Equal("", Status());
    }

    [Fact]
    public async Task Deleting_the_index_without_codegraph_leaves_a_committed_gitignore_in_place()
    {
        CommitIgnoreFile();
        CreateIndex();

        var response = await new UninitCodeGraphCommand(CreateCli(executable: null))
            .ExecuteAsync(new UninitCodeGraphRequest { Root = _repo });

        Assert.Equal("Removed", response.Outcome);
        Assert.Null(response.Message);
        Assert.True(File.Exists(IgnoreFile));
        Assert.Equal("", Status());
    }

    [Fact]
    public async Task Uninit_removes_the_whole_folder_when_git_tracks_nothing_in_it()
    {
        // Not committed: CodeGraph's own ignore file hides the folder, and nothing is put back.
        Directory.CreateDirectory(IndexFolder);
        File.WriteAllText(IgnoreFile, "*\n!.gitignore\n");
        GitConfigTestSupport.Git(_repo, "add -f .codegraph/.gitignore");
        GitConfigTestSupport.Git(_repo, "rm -q --cached .codegraph/.gitignore");
        CreateIndex();

        var response = await new UninitCodeGraphCommand(CreateCli(Executable))
            .ExecuteAsync(new UninitCodeGraphRequest { Root = _repo });

        Assert.Equal("Removed", response.Outcome);
        Assert.Null(response.Message);
        Assert.False(Directory.Exists(IndexFolder));
        Assert.Equal("", Status());
    }

    private void CommitIgnoreFile()
    {
        Directory.CreateDirectory(IndexFolder);
        File.WriteAllText(IgnoreFile, "*\n!.gitignore\n");
        GitConfigTestSupport.Git(_repo, "add -f .codegraph/.gitignore");
        GitConfigTestSupport.Git(_repo, "commit -q -m \"Track the CodeGraph ignore file\"");
    }

    private void CreateIndex() =>
        File.WriteAllText(Path.Combine(IndexFolder, CodeGraphCli.DatabaseFileName), "");

    private string Status() => GitConfigTestSupport.Git(_repo, "status --porcelain").Trim();

    private static CodeGraphCli CreateCli(string? executable) =>
        new(new UninitDeletesFolderCommandLine(), new StubLifetime(), NullLogger<CodeGraphCli>.Instance)
        {
            ExecutableResolver = () => executable,
            ProcessKiller = (_, _, _) => Task.FromResult(false),
            DeleteAttempts = 2,
            DeleteRetryDelay = TimeSpan.Zero,
        };

    /// <summary>
    /// Git runs for real. <c>codegraph uninit</c> is replaced by what CodeGraph 1.6.2 does: it deletes the whole
    /// <c>.codegraph</c> folder of its working directory, tracked files included.
    /// </summary>
    private sealed class UninitDeletesFolderCommandLine : ICommandLineService
    {
        private readonly CommandLineService _real =
            new(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));

        public Task<CommandLineResult> RunAsync(
            string fileName,
            string arguments,
            string? workingDirectory = null,
            string? stdin = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null, TimeSpan? idleTimeout = null) =>
            throw new NotSupportedException();

        public Task<CommandLineResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            byte[]? stdinBytes = null,
            CancellationToken cancellationToken = default,
            bool streamStderrAsStdout = false,
            bool mirrorFailureOutputAsStderr = false,
            TimeSpan? timeout = null, TimeSpan? idleTimeout = null)
        {
            if (!string.Equals(fileName, Executable, StringComparison.Ordinal))
            {
                return _real.RunAsync(
                    fileName, arguments, workingDirectory, stdinBytes, cancellationToken,
                    streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout, idleTimeout);
            }

            Assert.Equal("uninit", arguments[0]);
            Directory.Delete(Path.Combine(workingDirectory!, CodeGraphCli.DataFolderName), recursive: true);
            return Task.FromResult(new CommandLineResult(0, "", ""));
        }
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
