using GrayMoon.Agent.Commands;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Models;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Tests;

/// <summary>
/// A repository GitVersion cannot read (the usual case: a freshly created, still empty hosted repository
/// that was cloned and has no commits yet) must still sync. The version comes back unresolved, but the
/// branch survives, the sync succeeds, and the failure is reported as <c>GitVersionError</c> rather than
/// sinking the repository. Real git, real GitVersion.
/// </summary>
public sealed class SyncRepositoryGitVersionFailureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-sync-").FullName;
    private readonly SyncRepositoryCommand _command;

    public SyncRepositoryGitVersionFailureTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var git = new GitService(Options.Create(new AgentOptions()), NullLogger<GitService>.Instance, runner);
        _command = new SyncRepositoryCommand(git, new NoProjects());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort; git marks some files read-only */ }
    }

    [FactIfGitVersion]
    public async Task Empty_repository_syncs_with_an_unresolved_version_and_keeps_its_branch()
    {
        var repoPath = await CloneEmptyOriginAsync();

        var response = await SyncAsync();

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("-", response.Version);
        Assert.False(string.IsNullOrWhiteSpace(response.GitVersionError));
        Assert.Equal(await CurrentBranchAsync(repoPath), response.Branch);
        Assert.NotEqual("-", response.Branch);
        Assert.NotNull(response.RemoteBranches);
        Assert.Empty(response.RemoteBranches!);
        Assert.Null(response.DefaultBranch);
    }

    [FactIfGitVersion]
    public async Task Committed_but_unpushed_repository_resolves_its_version_while_the_remote_stays_empty()
    {
        var repoPath = await CloneEmptyOriginAsync();
        await File.WriteAllTextAsync(Path.Combine(repoPath, "README.md"), "hello\n");
        await GitVersionParityTests.RunProcessAsync("git", "add -A", repoPath);
        var (commitExit, _, commitErr) = await GitVersionParityTests.RunProcessAsync("git", "commit -m init", repoPath);
        Assert.True(commitExit == 0, commitErr);

        var response = await SyncAsync();

        Assert.True(response.Success, response.ErrorMessage);
        Assert.NotEqual("-", response.Version);
        Assert.Null(response.GitVersionError);
        Assert.Equal(await CurrentBranchAsync(repoPath), response.Branch);
        Assert.Empty(response.RemoteBranches!);
        Assert.Null(response.DefaultBranch);
    }

    private Task<GrayMoon.Agent.Jobs.Response.SyncRepositoryResponse> SyncAsync()
        => _command.ExecuteAsync(new SyncRepositoryRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "repo",
            RepositoryId = 1,
            WorkspaceId = 1,
        });

    private async Task<string> CloneEmptyOriginAsync()
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

    private sealed class NoProjects : ICsProjFileService
    {
        public Task<IReadOnlyList<CsProjFileInfo>> FindAsync(string repoPath, CancellationToken cancellationToken = default, int? maxParallel = null)
            => Task.FromResult<IReadOnlyList<CsProjFileInfo>>([]);

        public Task<IReadOnlyList<string>> GetProjectPathsAsync(string repoPath, CancellationToken cancellationToken = default, int? maxParallel = null)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<CsProjFileInfo?> ParseAsync(string csprojPath, CancellationToken cancellationToken = default)
            => Task.FromResult<CsProjFileInfo?>(null);

        public Task<int> UpdatePackageVersionsAsync(
            string repoPath,
            IReadOnlyList<(string ProjectPath, IReadOnlyDictionary<string, string> PackageUpdates)> projectUpdates,
            CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }
}
