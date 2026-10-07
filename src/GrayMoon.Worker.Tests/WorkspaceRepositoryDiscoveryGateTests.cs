using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// D6: the Workspace-root repository never discovers projects, whatever the capabilities say. Real git over
/// a temp clone, with a counting csproj scanner, in the style of <see cref="SyncRepositoryCapabilitiesTests"/>.
/// </summary>
public sealed class WorkspaceRepositoryDiscoveryGateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-gate-").FullName;
    private readonly GitService _git;
    private GitCliRepositoryReader _reader = null!;

    public WorkspaceRepositoryDiscoveryGateTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader, new LibGit2SharpGitIgnoreService());
    }

    public void Dispose()
    {
        var root = OperatingSystem.IsWindows() ? @"\\?\" + _root : _root;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Workspace_repository_never_calls_the_csproj_scanner_and_reports_no_projects()
    {
        // The Workspace repository's working tree is the workspace folder itself.
        await CloneCommittedRepositoryAsync(cloneIntoWorkspaceRoot: true);
        var scanner = new CountingCsProjFileService();

        var response = await SyncAsync(scanner, "Repo", workspaceRepositoryName: "repo");

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(0, scanner.FindCalls);
        Assert.Null(response.Projects);
    }

    [Fact]
    public async Task Other_repository_still_calls_the_csproj_scanner_when_a_workspace_repository_is_set()
    {
        await CloneCommittedRepositoryAsync(cloneIntoWorkspaceRoot: false);
        var scanner = new CountingCsProjFileService();

        var response = await SyncAsync(scanner, "repo", workspaceRepositoryName: "Workspace-Repo");

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(1, scanner.FindCalls);
        Assert.NotNull(response.Projects);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task Probe_discovers_projects_only_when_not_the_workspace_repository(bool isWorkspaceRepository, int expectedScans)
    {
        await CloneCommittedRepositoryAsync(cloneIntoWorkspaceRoot: false);
        var repoPath = Path.Combine(_root, "ws", "repo");
        var scanner = new CountingCsProjFileService();
        var probe = new RepositoryStateProbe(_reader, scanner, CapabilityTestDoubles.RealFactory(_git));

        var capture = await probe.CaptureAsync(repoPath, new RepositoryStateProbeOptions
        {
            IncludeProjects = true,
            Capabilities = RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: true),
            IsWorkspaceRepository = isWorkspaceRepository,
        });

        Assert.Equal(expectedScans, scanner.FindCalls);
        Assert.Equal(!isWorkspaceRepository, capture.Snapshot.ProjectsProbed);
    }

    private Task<GrayMoon.Worker.Jobs.Response.SyncRepositoryResponse> SyncAsync(
        CountingCsProjFileService scanner, string repositoryName, string? workspaceRepositoryName)
        => new SyncRepositoryCommand(_git, _reader, scanner, CapabilityTestDoubles.RealFactory(_git)).ExecuteAsync(new SyncRepositoryRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = repositoryName,
            WorkspaceRepositoryName = workspaceRepositoryName,
            RepositoryId = 1,
            WorkspaceId = 1,
            Capabilities = RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: true),
        });

    private async Task CloneCommittedRepositoryAsync(bool cloneIntoWorkspaceRoot)
    {
        var origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(origin);
        await RunGitAsync(origin, "init --bare -b main");

        var workspace = Path.Combine(_root, "ws");
        var repoPath = cloneIntoWorkspaceRoot ? workspace : Path.Combine(workspace, "repo");
        Directory.CreateDirectory(workspace);
        if (cloneIntoWorkspaceRoot)
            await RunGitAsync(_root, $"clone \"{origin}\" ws");
        else
            await RunGitAsync(workspace, $"clone \"{origin}\" repo");

        await RunGitAsync(repoPath, "config user.email test@example.com");
        await RunGitAsync(repoPath, "config user.name Test");
        await File.WriteAllTextAsync(Path.Combine(repoPath, "README.md"), "hello\n");
        await RunGitAsync(repoPath, "add -A");
        await RunGitAsync(repoPath, "commit -m init");
    }

    private static async Task RunGitAsync(string workingDirectory, string args)
    {
        var (exit, _, stderr) = await GitVersionParityTests.RunProcessAsync("git", args, workingDirectory);
        if (exit != 0)
            throw new InvalidOperationException($"git {args} failed: {stderr}");
    }
}