using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// A workspace profile decides which of sync's three stages run. The common git snapshot always does; the
/// version and .NET project stages are enrichment the request has to ask for. Real git, real GitVersion
/// where a resolved version is the point of the test.
/// </summary>
public sealed class SyncRepositoryCapabilitiesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-caps-").FullName;
    private readonly GitService _git;
    private GitCliRepositoryReader _reader = null!;

    public SyncRepositoryCapabilitiesTests()
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
    public async Task Basic_workspace_without_versioning_syncs_git_and_runs_no_version_or_project_work()
    {
        var repoPath = await CloneCommittedRepositoryAsync();
        var (projectScanner, versionProviders) = NewDoubles();

        var response = await SyncAsync(
            projectScanner,
            versionProviders,
            RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false));

        Assert.True(response.Success, response.ErrorMessage);

        // The common snapshot is complete: identity, branch and tag lists, divergence.
        Assert.Equal(await CurrentBranchAsync(repoPath), response.Branch);
        Assert.NotNull(response.LocalBranches);
        Assert.NotEmpty(response.LocalBranches!);
        Assert.NotNull(response.RemoteBranches);
        Assert.NotNull(response.Tags);
        Assert.Null(response.GitFetchError);

        // Neither optional stage ran, and neither pretended to: the version is the wire placeholder with no
        // error alongside it, and the project list is absent rather than empty.
        Assert.Equal(0, versionProviders.VersionCalls);
        Assert.Equal(0, projectScanner.FindCalls);
        Assert.Equal("-", response.Version);
        Assert.Null(response.GitVersionError);
        Assert.Null(response.Projects);
    }

    [FactIfGitVersion]
    public async Task Basic_workspace_with_versioning_resolves_a_version_and_still_skips_the_project_scan()
    {
        await CloneCommittedRepositoryAsync();
        var (projectScanner, versionProviders) = NewDoubles();

        var response = await SyncAsync(
            projectScanner,
            versionProviders,
            RepositoryOperationCapabilities.For(calculateVersion: true, discoverProjects: false));

        Assert.True(response.Success, response.ErrorMessage);
        Assert.NotEqual("-", response.Version);
        Assert.Null(response.GitVersionError);
        Assert.Equal(1, versionProviders.VersionCalls);
        Assert.Equal(0, projectScanner.FindCalls);
        Assert.Null(response.Projects);
    }

    [FactIfGitVersion]
    public async Task DotNet_dependency_workspace_with_versioning_runs_both_enrichment_stages()
    {
        await CloneCommittedRepositoryAsync();
        var (projectScanner, versionProviders) = NewDoubles();

        var response = await SyncAsync(
            projectScanner,
            versionProviders,
            RepositoryOperationCapabilities.For(calculateVersion: true, discoverProjects: true));

        Assert.True(response.Success, response.ErrorMessage);
        Assert.NotEqual("-", response.Version);
        Assert.Equal(1, versionProviders.VersionCalls);
        Assert.Equal(1, projectScanner.FindCalls);
        Assert.NotNull(response.Projects);
    }

    [FactIfGitVersion]
    public async Task Unstated_capabilities_keep_the_full_pre_profile_enrichment()
    {
        await CloneCommittedRepositoryAsync();
        var (projectScanner, versionProviders) = NewDoubles();

        // An app that predates workspace profiles sends no capabilities at all. Losing a known version
        // because of that would be worse than one wasted probe, so unstated means everything on.
        var response = await SyncAsync(projectScanner, versionProviders, capabilities: null);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.NotEqual("-", response.Version);
        Assert.Equal(1, versionProviders.VersionCalls);
        Assert.Equal(1, projectScanner.FindCalls);
        Assert.NotNull(response.Projects);
    }

    [Fact]
    public async Task Project_free_dotnet_repository_reports_an_empty_scan_rather_than_no_scan()
    {
        await CloneCommittedRepositoryAsync();
        var (projectScanner, versionProviders) = NewDoubles();

        var response = await SyncAsync(
            projectScanner,
            versionProviders,
            RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: true));

        // The distinction the app's ProjectsProbed marker rests on: an empty list prunes, a missing one does
        // not. A .NET repository that genuinely has no projects must produce the first, not the second.
        Assert.Equal(1, projectScanner.FindCalls);
        Assert.NotNull(response.Projects);
        Assert.Empty(response.Projects!);
    }

    [Fact]
    public async Task Probe_reports_the_version_as_not_probed_when_the_workspace_does_not_version()
    {
        var repoPath = await CloneCommittedRepositoryAsync();
        var (projectScanner, versionProviders) = NewDoubles();
        var probe = new RepositoryStateProbe(_reader, projectScanner, versionProviders);

        var capture = await probe.CaptureAsync(repoPath, new RepositoryStateProbeOptions
        {
            IncludeGitVersion = true,
            IncludeProjects = true,
            Capabilities = RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: true)
        });

        Assert.Equal(0, versionProviders.VersionCalls);
        Assert.False(capture.Snapshot.GitVersionProbed);
        Assert.Null(capture.Snapshot.GitVersion);
        // Identity is a plain git fact and survives the skipped version stage.
        Assert.True(capture.Snapshot.IdentityProbed);
        Assert.Equal(await CurrentBranchAsync(repoPath), capture.Snapshot.BranchName);
        Assert.True(capture.Snapshot.ProjectsProbed);
    }

    [Fact]
    public async Task Probe_reports_projects_as_not_probed_when_the_workspace_does_not_discover_them()
    {
        var repoPath = await CloneCommittedRepositoryAsync();
        var (projectScanner, versionProviders) = NewDoubles();
        var probe = new RepositoryStateProbe(_reader, projectScanner, versionProviders);

        var capture = await probe.CaptureAsync(repoPath, new RepositoryStateProbeOptions
        {
            IncludeProjects = true,
            Capabilities = RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false)
        });

        Assert.Equal(0, projectScanner.FindCalls);
        Assert.False(capture.Snapshot.ProjectsProbed);
        Assert.Null(capture.Snapshot.Projects);
    }

    private (CountingCsProjFileService ProjectScanner, CountingVersionProviderFactory VersionProviders) NewDoubles()
        => (new CountingCsProjFileService(), CapabilityTestDoubles.RealFactory(_git));

    private Task<SyncRepositoryResponse> SyncAsync(
        ICsProjFileService projectScanner,
        IRepositoryVersionProviderFactory versionProviders,
        RepositoryOperationCapabilities? capabilities)
        => new SyncRepositoryCommand(_git, _reader, projectScanner, versionProviders).ExecuteAsync(new SyncRepositoryRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "repo",
            RepositoryId = 1,
            WorkspaceId = 1,
            Capabilities = capabilities,
        });

    /// <summary>A clone with one commit, so a version provider has something to read and fetch succeeds.</summary>
    private async Task<string> CloneCommittedRepositoryAsync()
    {
        var origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(origin);
        await RunGitAsync(origin, "init --bare -b main");

        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspace);
        await RunGitAsync(workspace, $"clone \"{origin}\" repo");

        var repoPath = Path.Combine(workspace, "repo");
        await RunGitAsync(repoPath, "config user.email test@example.com");
        await RunGitAsync(repoPath, "config user.name Test");
        await File.WriteAllTextAsync(Path.Combine(repoPath, "README.md"), "hello\n");
        await RunGitAsync(repoPath, "add -A");
        await RunGitAsync(repoPath, "commit -m init");
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
