using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

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
    private readonly GitService _git;
    private GitCliRepositoryReader _reader = null!;
    private readonly RepositoryStateProbe _probe;

    public SyncRepositoryGitVersionFailureTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader, new LibGit2SharpGitIgnoreService());
        var versionProviderFactory = new RepositoryVersionProviderFactory(
            new GitVersionRepositoryVersionProvider(_git),
            new NoRepositoryVersionProvider());
        _command = new SyncRepositoryCommand(_git, _reader, new LibGit2SharpLocalGitSnapshotReader(), new NoProjects(), versionProviderFactory);
        _probe = new RepositoryStateProbe(_reader, new NoProjects(), versionProviderFactory);
    }

    public void Dispose()
    {
        // The extended-length prefix is what lets the delete reach the 260+ character test path on Windows.
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

    [FactIfGitVersion]
    public async Task Probe_keeps_the_branch_and_reports_a_failed_gitversion_as_probed_and_empty()
    {
        var repoPath = await CloneEmptyOriginAsync();

        var capture = await _probe.CaptureAsync(repoPath, new RepositoryStateProbeOptions { IncludeGitVersion = true });

        Assert.True(capture.Snapshot.IdentityProbed);
        Assert.Equal(await CurrentBranchAsync(repoPath), capture.Snapshot.BranchName);
        // Probed with no value: the app clears a stale version instead of keeping it.
        Assert.True(capture.Snapshot.GitVersionProbed);
        Assert.Null(capture.Snapshot.GitVersion);
    }

    [FactIfGitVersion]
    public async Task Branch_helper_reads_git_when_gitversion_gave_none_and_prefers_gitversion_when_it_did()
    {
        var repoPath = await CloneEmptyOriginAsync();
        var current = await CurrentBranchAsync(repoPath);

        Assert.Equal(current, await _reader.ResolveBranchAsync(null, repoPath, CancellationToken.None));
        Assert.Equal(current, await _reader.ResolveBranchAsync(new GitVersionResult(), repoPath, CancellationToken.None));
        Assert.Equal("from-gitversion", await _reader.ResolveBranchAsync(new GitVersionResult { BranchName = "from-gitversion" }, repoPath, CancellationToken.None));
        Assert.Equal("escaped", await _reader.ResolveBranchAsync(new GitVersionResult { EscapedBranchName = "escaped" }, repoPath, CancellationToken.None));
    }

    /// <summary>
    /// The real-world trigger found during GATE-4: a file nested deeper than 260 characters makes GitVersion itself
    /// crash on Windows (LibGit2Sharp "path too long"). The repository must still sync with its branch intact.
    /// </summary>
    [FactIfGitVersion]
    public async Task Repository_with_a_path_over_260_characters_still_syncs_with_its_branch()
    {
        if (!OperatingSystem.IsWindows())
            return; // The 260 character limit is a Windows property.

        var repoPath = await CloneEmptyOriginAsync();
        var dir = Path.Combine(repoPath, "deep", string.Join(Path.DirectorySeparatorChar, Enumerable.Range(1, 8).Select(i => new string('d', 38) + i)));
        Directory.CreateDirectory(@"\\?\" + dir);
        await File.WriteAllTextAsync(@"\\?\" + Path.Combine(dir, "file-with-a-long-name-to-pass-the-limit.txt"), "deep\n");
        await RunGitAsync(repoPath, "-c core.longpaths=true add -A");
        await RunGitAsync(repoPath, "-c core.longpaths=true commit -m deep");

        var response = await SyncAsync();

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(await CurrentBranchAsync(repoPath), response.Branch);
        // Whatever GitVersion made of it, the answer is consistent: no version means a reported error.
        Assert.True(response.Version != "-" || !string.IsNullOrWhiteSpace(response.GitVersionError));
    }
    private Task<GrayMoon.Worker.Jobs.Response.SyncRepositoryResponse> SyncAsync()
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
