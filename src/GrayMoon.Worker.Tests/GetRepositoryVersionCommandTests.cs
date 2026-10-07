using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The sync-status poll has to tell "no version provider ran" from "one ran and failed", because the app
/// calls the second a mismatch and the first nothing at all. Real git; no GitVersion needed, since every case
/// here is either a skipped provider or a repository GitVersion cannot read.
/// </summary>
public sealed class GetRepositoryVersionCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-getver-").FullName;
    private readonly GitService _git;
    private GitCliRepositoryReader _reader = null!;
    private readonly CountingVersionProviderFactory _versionProviders;
    private readonly GetRepositoryVersionCommand _command;

    public GetRepositoryVersionCommandTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader, new LibGit2SharpGitIgnoreService());
        _versionProviders = CapabilityTestDoubles.RealFactory(_git);
        _command = new GetRepositoryVersionCommand(_reader, _versionProviders);
    }

    public void Dispose()
    {
        var root = OperatingSystem.IsWindows() ? @"\\?\" + _root : _root;
        try
        {
            // Git writes its object files read-only, which blocks the delete.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Workspace_without_versioning_reports_the_version_as_not_probed_and_keeps_the_branch()
    {
        var repoPath = await InitRepositoryAsync();

        var response = await ExecuteAsync(RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false));

        Assert.True(response.Exists);
        Assert.False(response.VersionProbed);
        Assert.Null(response.Version);
        Assert.Equal(0, _versionProviders.VersionCalls);
        Assert.Equal(await CurrentBranchAsync(repoPath), response.Branch);
    }

    [Fact]
    public async Task Version_provider_that_ran_and_failed_reports_probed_with_no_version()
    {
        var repoPath = await InitRepositoryAsync();

        // A repository with no commits: GitVersion cannot produce a version, and neither can a missing
        // GitVersion tool. Either way the provider ran, which is the distinction under test.
        var response = await ExecuteAsync(RepositoryOperationCapabilities.For(calculateVersion: true, discoverProjects: false));

        Assert.True(response.Exists);
        Assert.True(response.VersionProbed);
        Assert.Null(response.Version);
        Assert.Equal(1, _versionProviders.VersionCalls);
        Assert.Equal(await CurrentBranchAsync(repoPath), response.Branch);
    }

    [FactIfGitVersion]
    public async Task Version_provider_that_resolved_a_version_reports_probed_with_it()
    {
        var repoPath = await InitRepositoryAsync();
        await File.WriteAllTextAsync(Path.Combine(repoPath, "README.md"), "hello\n");
        await RunGitAsync(repoPath, "add -A");
        await RunGitAsync(repoPath, "commit -m init");

        var response = await ExecuteAsync(RepositoryOperationCapabilities.For(calculateVersion: true, discoverProjects: false));

        Assert.True(response.VersionProbed);
        Assert.False(string.IsNullOrWhiteSpace(response.Version));
    }

    [Fact]
    public async Task Missing_checkout_reports_neither_existence_nor_a_probe()
    {
        Directory.CreateDirectory(Path.Combine(_root, "ws"));

        var response = await ExecuteAsync(RepositoryOperationCapabilities.For(calculateVersion: true, discoverProjects: false));

        Assert.False(response.Exists);
        Assert.Null(response.VersionProbed);
        Assert.Equal(0, _versionProviders.VersionCalls);
    }

    private Task<GetRepositoryVersionResponse> ExecuteAsync(RepositoryOperationCapabilities? capabilities)
        => _command.ExecuteAsync(new GetRepositoryVersionRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "repo",
            Capabilities = capabilities,
        });

    private async Task<string> InitRepositoryAsync()
    {
        var repoPath = Path.Combine(_root, "ws", "repo");
        Directory.CreateDirectory(repoPath);
        await RunGitAsync(repoPath, "init -b main");
        await RunGitAsync(repoPath, "config user.email test@example.com");
        await RunGitAsync(repoPath, "config user.name Test");
        return repoPath;
    }

    private static async Task<string> CurrentBranchAsync(string repoPath)
    {
        var (exit, stdout, stderr) = await GitVersionParityTests.RunProcessAsync("git", "branch --show-current", repoPath);
        Assert.True(exit == 0, stderr);
        return stdout.Trim();
    }

    private static async Task RunGitAsync(string workingDirectory, string args)
    {
        var (exit, _, stderr) = await GitVersionParityTests.RunProcessAsync("git", args, workingDirectory);
        if (exit != 0)
            throw new InvalidOperationException($"git {args} failed: {stderr}");
    }
}
