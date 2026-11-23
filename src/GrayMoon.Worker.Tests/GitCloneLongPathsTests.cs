using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static GrayMoon.Worker.Tests.GitConfigTestSupport;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// On Windows a clone records <c>core.longpaths=true</c> in its own config, so GitVersion (libgit2, which refuses paths
/// over 260 characters without it) works in the repository and its Feature worktrees from the first sync on, whether
/// or not the Worker's configuration initializer runs. Before, the clone was started with <c>git -c ... clone</c>,
/// which applies the setting to the clone process only.
/// </summary>
public sealed class GitCloneLongPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-clone-lp-").FullName;
    private readonly GitService _git;

    public GitCloneLongPathsTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task Clone_into_a_workspace_folder_keeps_long_paths_in_the_new_repository_config()
    {
        var origin = GitScenarios.SeededRemote(Path.Combine(_root, "remote"));
        var workspace = Path.Combine(_root, "ws");

        var result = await _git.CloneAsync(workspace, origin, null, CancellationToken.None);

        Assert.True(result.Success);
        AssertLongPathsRecorded(Path.Combine(workspace, "origin"));
    }

    [Fact]
    public async Task Clone_into_a_given_folder_keeps_long_paths_in_the_new_repository_config()
    {
        var origin = GitScenarios.SeededRemote(Path.Combine(_root, "remote"));
        var target = Path.Combine(_root, "target");

        var result = await _git.CloneIntoAsync(target, origin, null, CancellationToken.None);

        Assert.True(result.Success);
        AssertLongPathsRecorded(target);
    }

    private static void AssertLongPathsRecorded(string repo)
    {
        var local = Git(repo, "config --local --get core.longpaths", allowFailure: true).Trim();
        Assert.Equal(OperatingSystem.IsWindows() ? "true" : "", local);
    }
}
