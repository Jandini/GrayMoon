using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Sync overlaps one lane of read-intent ref reads with GitVersion. These tests pin the contract that makes
/// that safe: read intent really bypasses the repository write lock, it returns what write intent returns,
/// and the ordering rules the overlap must not break (fetch first, divergence base before counts, tag
/// checkouts, failed fetch). Real git against a bare remote.
/// </summary>
public sealed class SyncRepositoryReadOverlapTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-overlap-").FullName;
    private readonly GitProcessRunner _runner;
    private readonly GitService _git;

    public SyncRepositoryReadOverlapTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        _runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, _runner);
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
    public async Task Read_intent_ignores_the_repository_write_lock()
    {
        var repoPath = await CloneCommittedRepositoryAsync();
        await RunGitAsync(repoPath, "tag v1.0.0");

        // Write-intent versions of these calls would wait for the lock held below and hang, so only the
        // read-intent versions are called inside it.
        await _runner.WithRepoWriteLockAsync(repoPath, async ct =>
        {
            await FinishesInTimeAsync(_git.GetTagsAsync(repoPath, ct, GitLockIntent.Read));
            await FinishesInTimeAsync(_git.GetLocalBranchesAsync(repoPath, ct, GitLockIntent.Read));
            await FinishesInTimeAsync(_git.GetCheckedOutTagAsync(repoPath, ct, GitLockIntent.Read));
            await FinishesInTimeAsync(_git.ProbeCommitCountsAsync(repoPath, "main", null, ct, intent: GitLockIntent.Read));
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Read_and_write_intent_agree()
    {
        var repoPath = await CloneCommittedRepositoryAsync();
        await RunGitAsync(repoPath, "tag v1.0.0");
        await RunGitAsync(repoPath, "branch other");
        const GitLockIntent read = GitLockIntent.Read;
        var ct = CancellationToken.None;

        Assert.Equal(await _git.GetCurrentBranchNameAsync(repoPath, ct), await _git.GetCurrentBranchNameAsync(repoPath, ct, read));
        Assert.Equal(await _git.GetCheckedOutTagAsync(repoPath, ct), await _git.GetCheckedOutTagAsync(repoPath, ct, read));
        Assert.Equal(await _git.GetTagsAsync(repoPath, ct), await _git.GetTagsAsync(repoPath, ct, read));
        Assert.Equal(await _git.GetLocalBranchesAsync(repoPath, ct), await _git.GetLocalBranchesAsync(repoPath, ct, read));
        Assert.Equal(await _git.GetRemoteBranchesFromRefsAsync(repoPath, ct), await _git.GetRemoteBranchesFromRefsAsync(repoPath, ct, read));

        var defaultRef = await _git.GetDefaultBranchOriginRefAsync(repoPath, ct);
        Assert.Equal(defaultRef, await _git.GetDefaultBranchOriginRefAsync(repoPath, ct, read));
        Assert.Equal(
            await _git.GetCommitCountsVsDefaultAsync(repoPath, defaultRef, ct),
            await _git.GetCommitCountsVsDefaultAsync(repoPath, defaultRef, ct, read));
        Assert.Equal(
            await _git.ProbeCommitCountsAsync(repoPath, "main", defaultRef, ct),
            await _git.ProbeCommitCountsAsync(repoPath, "main", defaultRef, ct, intent: read));
    }

    [Fact]
    public async Task Tag_checkout_reports_the_tag_clears_the_branch_and_still_installs_hooks()
    {
        var repoPath = await CloneCommittedRepositoryAsync();
        await RunGitAsync(repoPath, "tag v1.0.0");
        await RunGitAsync(repoPath, "push origin v1.0.0");
        await RunGitAsync(repoPath, "checkout --detach v1.0.0");

        var response = await SyncAsync(RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false));

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("-", response.Branch);
        Assert.Equal("v1.0.0", response.Tag);
        Assert.Contains("v1.0.0", response.Tags!);
        Assert.Null(response.OutgoingCommits);
        Assert.Null(response.IncomingCommits);
        Assert.True(File.Exists(Path.Combine(repoPath, ".git", "hooks", "post-checkout")), "post-checkout hook was not installed.");
    }

    [Fact]
    public async Task Failed_fetch_starts_no_version_or_project_work()
    {
        var repoPath = await CloneCommittedRepositoryAsync();
        await RunGitAsync(repoPath, $"remote set-url origin \"{Path.Combine(_root, "does-not-exist.git")}\"");
        var projectScanner = new CountingCsProjFileService();
        var versionProviders = CapabilityTestDoubles.RealFactory(_git);

        var response = await SyncAsync(
            RepositoryOperationCapabilities.For(calculateVersion: true, discoverProjects: true),
            projectScanner,
            versionProviders);

        Assert.False(response.Success);
        Assert.NotNull(response.GitFetchError);
        Assert.Equal("-", response.Version);
        Assert.Null(response.Projects);
        Assert.Equal(0, versionProviders.VersionCalls);
        Assert.Equal(0, projectScanner.FindCalls);
    }

    [Fact]
    public async Task Divergence_base_is_written_before_the_upstream_counts_read_it()
    {
        var repoPath = await CloneCommittedRepositoryAsync();

        // main is the default branch; parent is one commit ahead of it; feature is one commit ahead of parent
        // and has no upstream. Counted against the divergence base the answer is 1, against the default 2.
        await RunGitAsync(repoPath, "checkout -b parent");
        await CommitAsync(repoPath, "parent.txt", "parent commit");
        await RunGitAsync(repoPath, "checkout -b feature");
        await CommitAsync(repoPath, "feature.txt", "feature commit");

        var request = NewRequest(RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false));
        request.DivergenceBaseBranch = "parent";
        var response = await new SyncRepositoryCommand(_git, new CountingCsProjFileService(), CapabilityTestDoubles.RealFactory(_git))
            .ExecuteAsync(request);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("feature", response.Branch);
        Assert.Equal(1, response.OutgoingCommits);
    }

    [Fact]
    public async Task Version_providers_branch_wins_over_the_branch_in_git()
    {
        await CloneCommittedRepositoryAsync();
        var factory = new FixedVersionProviderFactory(new RepositoryVersionResult(
            Probed: true,
            new GitVersionResult { InformationalVersion = "1.2.3", BranchName = "from-gitversion" },
            Error: null));

        var response = await SyncAsync(RepositoryOperationCapabilities.For(calculateVersion: true, discoverProjects: false), versionProviders: factory);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("from-gitversion", response.Branch);
        Assert.Equal("1.2.3", response.Version);
    }

    [Fact]
    public async Task Branch_comes_from_git_when_the_version_provider_gives_none()
    {
        await CloneCommittedRepositoryAsync();
        var factory = new FixedVersionProviderFactory(new RepositoryVersionResult(Probed: true, Result: null, Error: "boom"));

        var response = await SyncAsync(RepositoryOperationCapabilities.For(calculateVersion: true, discoverProjects: false), versionProviders: factory);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("main", response.Branch);
        Assert.Equal("boom", response.GitVersionError);
        Assert.NotNull(response.LocalBranches);
        Assert.Equal(0, response.OutgoingCommits);
    }

    [Fact]
    public async Task Sync_logs_where_the_time_went()
    {
        await CloneCommittedRepositoryAsync();
        var log = new CapturingLogger();

        var response = await new SyncRepositoryCommand(_git, new CountingCsProjFileService(), CapabilityTestDoubles.RealFactory(_git), log)
            .ExecuteAsync(NewRequest(RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false)));

        Assert.True(response.Success, response.ErrorMessage);
        var line = Assert.Single(log.Messages, m => m.Contains("SyncRepository timings", StringComparison.Ordinal));
        foreach (var part in new[] { "fetch=", "version=", "lane=", "tail=", "total=", "Lane steps:", "branch=", "tags=", "defaultCounts=" })
            Assert.Contains(part, line, StringComparison.Ordinal);
    }

    private sealed class FixedVersionProviderFactory(RepositoryVersionResult result) : IRepositoryVersionProviderFactory
    {
        public IRepositoryVersionProvider Create(RepositoryOperationCapabilities? capabilities) => new Provider(result);

        private sealed class Provider(RepositoryVersionResult result) : IRepositoryVersionProvider
        {
            public Task<RepositoryVersionResult> GetVersionAsync(string repoPath, RepositoryVersionOptions options, CancellationToken ct = default)
                => Task.FromResult(result);
        }
    }

    private sealed class CapturingLogger : ILogger<SyncRepositoryCommand>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private static async Task FinishesInTimeAsync(Task task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(Timeout));
        Assert.Same(task, finished);
        await task;
    }

    private Task<SyncRepositoryResponse> SyncAsync(
        RepositoryOperationCapabilities capabilities,
        ICsProjFileService? projectScanner = null,
        IRepositoryVersionProviderFactory? versionProviders = null)
        => new SyncRepositoryCommand(
                _git,
                projectScanner ?? new CountingCsProjFileService(),
                versionProviders ?? CapabilityTestDoubles.RealFactory(_git))
            .ExecuteAsync(NewRequest(capabilities));

    private SyncRepositoryRequest NewRequest(RepositoryOperationCapabilities capabilities) => new()
    {
        WorkspaceRoot = _root,
        WorkspaceName = "ws",
        RepositoryName = "repo",
        RepositoryId = 1,
        WorkspaceId = 1,
        Capabilities = capabilities,
    };

    /// <summary>A clone with one pushed commit on main, so fetch succeeds and main has an upstream.</summary>
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
        await CommitAsync(repoPath, "README.md", "init");
        await RunGitAsync(repoPath, "push -u origin main");
        return repoPath;
    }

    private static async Task CommitAsync(string repoPath, string fileName, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repoPath, fileName), message + "\n");
        await RunGitAsync(repoPath, "add -A");
        await RunGitAsync(repoPath, $"commit -m \"{message}\"");
    }

    private static async Task RunGitAsync(string workingDirectory, string args)
    {
        var (exit, _, stderr) = await GitVersionParityTests.RunProcessAsync("git", args, workingDirectory);
        if (exit != 0)
            throw new InvalidOperationException($"git {args} failed: {stderr}");
    }
}
