using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The four git hooks are the one Worker-initiated path, so they have no app request to read capabilities
/// from and resolve them through <see cref="IWorkspaceCapabilityProvider"/> instead. A Basic workspace that
/// does not version its repositories must therefore launch no version provider and scan no project file,
/// whichever hook the developer's own git invocation happened to fire.
/// </summary>
public sealed class HookSyncCapabilitiesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-hook-caps-").FullName;
    private readonly GitService _git;

    public HookSyncCapabilitiesTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner);
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

    [Theory]
    [InlineData(NotifyHookKind.Commit)]
    [InlineData(NotifyHookKind.Checkout)]
    [InlineData(NotifyHookKind.Merge)]
    [InlineData(NotifyHookKind.Push)]
    public async Task Basic_workspace_without_versioning_runs_no_version_or_project_work_through_any_hook(NotifyHookKind hookKind)
    {
        var repoPath = await CloneCommittedRepositoryAsync();
        var harness = NewHarness(RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false));

        await harness.Dispatcher.ExecuteAsync(NewJob(repoPath, hookKind));

        Assert.Equal(0, harness.VersionProviders.VersionCalls);
        Assert.Equal(0, harness.ProjectScanner.FindCalls);
        // Every hook asked, rather than one of them quietly assuming full enrichment.
        Assert.Equal(1, harness.Capabilities.Calls);
    }

    [FactIfGitVersion]
    public async Task A_versioning_dotnet_workspace_still_enriches_through_the_merge_hook()
    {
        var repoPath = await CloneCommittedRepositoryAsync();
        var harness = NewHarness(RepositoryOperationCapabilities.For(calculateVersion: true, discoverProjects: true));

        await harness.Dispatcher.ExecuteAsync(NewJob(repoPath, NotifyHookKind.Merge));

        Assert.Equal(1, harness.VersionProviders.VersionCalls);
        Assert.Equal(1, harness.ProjectScanner.FindCalls);
    }

    [Fact]
    public async Task An_unreachable_app_does_not_stop_the_hook_and_falls_back_to_full_enrichment()
    {
        var repoPath = await CloneCommittedRepositoryAsync();
        var projectScanner = new CountingCsProjFileService();
        var versionProviders = CapabilityTestDoubles.RealFactory(_git);
        var probe = new RepositoryStateProbe(_git, projectScanner, versionProviders);

        // The real provider, pointed at a port nothing is listening on. Never fail a hook sync because
        // capabilities could not be resolved: an existing .NET workspace losing its version because the app
        // was briefly down is worse than one wasted probe in a Basic one.
        var capabilityProvider = new WorkspaceCapabilityProvider(
            Options.Create(new WorkerOptions { AppApiBaseUrl = $"http://127.0.0.1:{FakeAppApi.FindClosedPort()}" }),
            new FakeWorkerSecretProvider("worker-secret-value"),
            NullLogger<WorkspaceCapabilityProvider>.Instance);

        var hook = new MergeHookSyncCommand(probe, capabilityProvider, new DisconnectedHubProvider(), NullLogger<MergeHookSyncCommand>.Instance);
        await hook.ExecuteAsync(NewJob(repoPath, NotifyHookKind.Merge));

        Assert.Equal(1, projectScanner.FindCalls);
    }

    [Fact]
    public async Task The_pre_push_hook_does_not_report_commit_counts_it_could_not_have_read_yet()
    {
        var repoPath = await CloneCommittedRepositoryAsync();
        var projectScanner = new CountingCsProjFileService();
        var probe = new RepositoryStateProbe(_git, projectScanner, CapabilityTestDoubles.RealFactory(_git));

        // The pre-push pass runs before the push data is transferred, so counts read now are stale. They
        // must stay unprobed, or the app would persist them over the real ones.
        var (state, _) = await probe.CaptureAsync(repoPath, new RepositoryStateProbeOptions
        {
            IncludeGitVersion = true,
            IncludeCommitCounts = false,
            Capabilities = RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false)
        });

        Assert.True(state.IdentityProbed);
        Assert.False(state.CommitCountsProbed);
        Assert.False(state.UpstreamProbed);
        Assert.False(state.GitVersionProbed);
        Assert.False(state.ProjectsProbed);
    }

    private sealed record Harness(
        HookSyncDispatcher Dispatcher,
        CountingCsProjFileService ProjectScanner,
        CountingVersionProviderFactory VersionProviders,
        FakeWorkspaceCapabilityProvider Capabilities);

    private Harness NewHarness(RepositoryOperationCapabilities capabilities)
    {
        var projectScanner = new CountingCsProjFileService();
        var versionProviders = CapabilityTestDoubles.RealFactory(_git);
        var probe = new RepositoryStateProbe(_git, projectScanner, versionProviders);
        var capabilityProvider = new FakeWorkspaceCapabilityProvider(capabilities);
        var hubProvider = new DisconnectedHubProvider();
        var tokenProvider = new NoWorkerToken();

        var dispatcher = new HookSyncDispatcher(
            new CheckoutHookSyncCommand(_git, probe, tokenProvider, capabilityProvider, hubProvider, NullLogger<CheckoutHookSyncCommand>.Instance),
            new CommitHookSyncCommand(probe, capabilityProvider, hubProvider, NullLogger<CommitHookSyncCommand>.Instance),
            new MergeHookSyncCommand(probe, capabilityProvider, hubProvider, NullLogger<MergeHookSyncCommand>.Instance),
            new PushHookSyncCommand(_git, probe, capabilityProvider, hubProvider, NullLogger<PushHookSyncCommand>.Instance));

        return new Harness(dispatcher, projectScanner, versionProviders, capabilityProvider);
    }

    private static NotifySyncJob NewJob(string repoPath, NotifyHookKind hookKind) => new()
    {
        WorkspaceId = 1,
        RepositoryId = 1,
        RepositoryPath = repoPath,
        HookKind = hookKind,
    };

    /// <summary>A clone with one commit, so the probe has a real branch and HEAD to read.</summary>
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

    private static async Task RunGitAsync(string workingDirectory, string args)
    {
        var (exit, _, stderr) = await GitVersionParityTests.RunProcessAsync("git", args, workingDirectory);
        if (exit != 0)
            throw new InvalidOperationException($"git {args} failed: {stderr}");
    }
}
