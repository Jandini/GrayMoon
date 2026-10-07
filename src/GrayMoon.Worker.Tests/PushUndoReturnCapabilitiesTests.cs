using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Return-to-default, undo-push and push refresh a repository after the git work is done. That refresh
/// honours the request's capabilities: a workspace that does not version launches no GitVersion, one that
/// does not discover .NET projects scans no project file, and a skipped step reports itself as not probed.
/// Real git against a local origin.
/// </summary>
public sealed class PushUndoReturnCapabilitiesTests : IDisposable
{
    private static readonly RepositoryOperationCapabilities BasicWithoutVersioning
        = RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false);

    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-push-caps-").FullName;
    private readonly GitService _git;
    private GitCliRepositoryReader _reader = null!;

    public PushUndoReturnCapabilitiesTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader);
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
    public async Task Return_to_default_for_basic_without_versioning_launches_no_gitversion_and_scans_no_projects()
    {
        await CloneOnFeatureBranchAsync();
        var (projectScanner, versionProviders) = NewDoubles();

        var response = await ReturnToDefaultAsync(projectScanner, versionProviders, BasicWithoutVersioning);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("main", response.CurrentBranch);
        Assert.Equal(0, versionProviders.VersionCalls);
        Assert.Equal(0, projectScanner.FindCalls);
        Assert.NotNull(response.State);
        Assert.False(response.State!.GitVersionProbed);
        Assert.False(response.State.ProjectsProbed);
        Assert.True(response.State.IdentityProbed);
    }

    [Fact]
    public async Task Return_to_default_for_dotnet_without_versioning_still_scans_projects()
    {
        await CloneOnFeatureBranchAsync();
        var (projectScanner, versionProviders) = NewDoubles();

        var response = await ReturnToDefaultAsync(
            projectScanner,
            versionProviders,
            RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: true));

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(0, versionProviders.VersionCalls);
        Assert.Equal(1, projectScanner.FindCalls);
        Assert.True(response.State!.ProjectsProbed);
    }

    [FactIfGitVersion]
    public async Task Return_to_default_with_unstated_capabilities_keeps_the_full_refresh()
    {
        await CloneOnFeatureBranchAsync();
        var (projectScanner, versionProviders) = NewDoubles();

        var response = await ReturnToDefaultAsync(projectScanner, versionProviders, capabilities: null);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(1, versionProviders.VersionCalls);
        Assert.Equal(1, projectScanner.FindCalls);
        Assert.True(response.State!.GitVersionProbed);
    }

    [Fact]
    public async Task Undo_push_refresh_for_basic_without_versioning_launches_no_gitversion()
    {
        var repoPath = await CloneOnFeatureBranchAsync();
        var (_, versionProviders) = NewDoubles();
        var command = new UndoPushCommand(_git, _reader, versionProviders, new DisconnectedHubProvider(), NullLogger<UndoPushCommand>.Instance);

        var notification = await command.BuildPostResetNotificationAsync(
            new UndoPushRequest { WorkspaceId = 1, RepositoryId = 2, Capabilities = BasicWithoutVersioning },
            repoPath,
            "feature/x");

        Assert.Equal(0, versionProviders.VersionCalls);
        Assert.Equal("-", notification.Version);
        Assert.Equal("feature/x", notification.Branch);
        Assert.False(notification.State!.GitVersionProbed);
        Assert.Null(notification.State.GitVersion);
    }

    [Fact]
    public async Task Push_refresh_for_basic_without_versioning_launches_no_gitversion_and_scans_no_projects()
    {
        var repoPath = await CloneOnFeatureBranchAsync();
        var (projectScanner, versionProviders) = NewDoubles();

        var notification = await NewPushCommand(projectScanner, versionProviders).BuildPostOperationNotificationAsync(
            new PushRepositoryRequest { WorkspaceId = 1, RepositoryId = 2, Capabilities = BasicWithoutVersioning },
            repoPath,
            "feature/x",
            versionOnly: false);

        Assert.Equal(0, versionProviders.VersionCalls);
        Assert.Equal(0, projectScanner.FindCalls);
        Assert.Equal("-", notification.Version);
        Assert.Null(notification.Projects);
        Assert.False(notification.State!.GitVersionProbed);
        Assert.False(notification.State.ProjectsProbed);
        Assert.True(notification.State.CommitCountsProbed);
    }

    [Fact]
    public async Task Push_refresh_for_dotnet_without_versioning_still_scans_projects()
    {
        var repoPath = await CloneOnFeatureBranchAsync();
        var (projectScanner, versionProviders) = NewDoubles();

        var notification = await NewPushCommand(projectScanner, versionProviders).BuildPostOperationNotificationAsync(
            new PushRepositoryRequest
            {
                WorkspaceId = 1,
                RepositoryId = 2,
                Capabilities = RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: true),
            },
            repoPath,
            "feature/x",
            versionOnly: true);

        Assert.Equal(0, versionProviders.VersionCalls);
        Assert.Equal(1, projectScanner.FindCalls);
        Assert.NotNull(notification.Projects);
        Assert.True(notification.State!.ProjectsProbed);
        Assert.False(notification.State.GitVersionProbed);
    }

    private (CountingCsProjFileService ProjectScanner, CountingVersionProviderFactory VersionProviders) NewDoubles()
        => (new CountingCsProjFileService(), CapabilityTestDoubles.RealFactory(_git));

    private PushRepositoryCommand NewPushCommand(CountingCsProjFileService projectScanner, CountingVersionProviderFactory versionProviders)
        => new(_git, _reader, projectScanner, versionProviders, new GitRemoteIntegrateService(_git, _reader), new DisconnectedHubProvider(), NullLogger<PushRepositoryCommand>.Instance);

    private Task<Jobs.Response.ReturnToDefaultBranchResponse> ReturnToDefaultAsync(
        CountingCsProjFileService projectScanner,
        CountingVersionProviderFactory versionProviders,
        RepositoryOperationCapabilities? capabilities)
    {
        var probe = new RepositoryStateProbe(_reader, projectScanner, versionProviders);
        var command = new ReturnToDefaultBranchCommand(_git, _reader, probe, NullLogger<ReturnToDefaultBranchCommand>.Instance);
        return command.ExecuteAsync(new ReturnToDefaultBranchRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "repo",
            CurrentBranchName = "feature/x",
            ForceDeleteLocalBranch = true,
            Capabilities = capabilities,
        });
    }

    /// <summary>A clone of an origin whose main has one commit, checked out on a local feature branch.</summary>
    private async Task<string> CloneOnFeatureBranchAsync()
    {
        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seed);
        await RunGitAsync(seed, "init -b main");
        await RunGitAsync(seed, "config user.email test@example.com");
        await RunGitAsync(seed, "config user.name Test");
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "hello\n");
        await RunGitAsync(seed, "add -A");
        await RunGitAsync(seed, "commit -m init");

        var origin = Path.Combine(_root, "origin.git");
        await RunGitAsync(_root, $"clone --bare \"{seed}\" \"{origin}\"");

        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspace);
        await RunGitAsync(workspace, $"clone \"{origin}\" repo");

        var repoPath = Path.Combine(workspace, "repo");
        await RunGitAsync(repoPath, "config user.email test@example.com");
        await RunGitAsync(repoPath, "config user.name Test");
        await RunGitAsync(repoPath, "checkout -b feature/x");
        return repoPath;
    }

    private static async Task RunGitAsync(string workingDirectory, string args)
    {
        var (exit, _, stderr) = await GitVersionParityTests.RunProcessAsync("git", args, workingDirectory);
        if (exit != 0)
            throw new InvalidOperationException($"git {args} failed: {stderr}");
    }
}
