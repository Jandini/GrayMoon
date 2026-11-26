using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>Holds <see cref="GitProcessCounter"/> to what the command-count gates rely on.</summary>
public sealed class GitProcessCounterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-gpc-").FullName;

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task Counts_every_git_process_of_a_read_by_marker_and_by_folder()
    {
        var repoPath = GitConfigTestSupport.CreateRepo(_root, "repo");
        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var counter = new GitProcessCounter(inner);
        var runner = new GitProcessRunner(counter, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);

        var head = await reader.GetHeadCommitAsync(repoPath, CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(head));
        Assert.True(counter.Count >= 1, "Reading HEAD starts at least one git process.");
        Assert.Equal(counter.Count, counter.In(repoPath).Count);
        Assert.Equal(0, counter.CountOf("worktree list"));
        Assert.Equal(counter.Count, counter.Describe().Split(Environment.NewLine).Length);

        counter.Clear();
        Assert.Equal(0, counter.Count);
    }
}
