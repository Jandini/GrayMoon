using System.Text.Json;
using GrayMoon.Abstractions.Worker;
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
/// Worktree creation carries the workspace profile so the <c>post-checkout</c> hook it fires knows what to
/// skip, and the standalone project refresh honours the profile the same way sync does.
/// </summary>
public sealed class WorktreeAndProjectRefreshCapabilitiesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-wt-caps-").FullName;
    private readonly GitService _git;

    public WorktreeAndProjectRefreshCapabilitiesTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Create_worktree_request_reads_the_capabilities_the_app_sends()
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            workspaceRoot = _root,
            mainRepositoryPath = Path.Combine(_root, "ws", "repo"),
            worktreePath = Path.Combine(_root, "ws.features", "f1", "repo"),
            branchName = "f1",
            workspaceId = 7,
            repositoryId = 3,
            baseCommitSha = "abc123",
            capabilities = new { calculateRepositoryVersion = false, discoverDotNetProjects = false },
        });

        var job = new CommandJobFactory().CreateCommandJob("r1", WorkerHubMethods.CreateGitWorktree, args);

        var request = Assert.IsType<CreateGitWorktreeRequest>(job.CommandJob!.Request);
        Assert.IsAssignableFrom<WorkspaceCommandRequest>(request);
        Assert.Equal(7, request.WorkspaceId);
        Assert.NotNull(request.Capabilities);
        Assert.False(request.EffectiveCapabilities.ShouldCalculateVersion);
        Assert.False(request.EffectiveCapabilities.ShouldDiscoverProjects);
    }

    [Fact]
    public async Task Create_worktree_warms_the_capability_cache_the_post_checkout_hook_reads()
    {
        // No app URL: a cold miss would fall back to full enrichment, so a Basic answer can only come from
        // the cache the dispatcher warmed.
        var provider = new WorkspaceCapabilityProvider(
            Options.Create(new WorkerOptions { AppApiBaseUrl = null }),
            new FakeWorkerSecretProvider("secret"),
            NullLogger<WorkspaceCapabilityProvider>.Instance);
        var dispatcher = CreateDispatcher(provider);
        var request = new CreateGitWorktreeRequest
        {
            WorkspaceId = 7,
            Capabilities = RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false),
        };

        // An unknown command name still warms the cache first and then fails without touching any handler,
        // which keeps the test free of a real worktree.
        await Assert.ThrowsAsync<NotSupportedException>(() => dispatcher.ExecuteAsync("NotACommand", request));

        var hookCapabilities = await provider.GetAsync(7);
        Assert.False(hookCapabilities.ShouldCalculateVersion);
        Assert.False(hookCapabilities.ShouldDiscoverProjects);
    }

    [Fact]
    public async Task Basic_project_refresh_scans_nothing_and_reports_no_project_list()
    {
        CreateRepositoryDirectory();
        var scanner = new CountingCsProjFileService();
        var command = new RefreshRepositoryProjectsCommand(_git, scanner);

        var response = await command.ExecuteAsync(NewRefreshRequest(
            RepositoryOperationCapabilities.For(calculateVersion: true, discoverProjects: false)));

        Assert.Equal(0, scanner.FindCalls);
        Assert.Null(response.Projects);
    }

    [Fact]
    public async Task DotNet_project_refresh_still_scans_the_repository()
    {
        CreateRepositoryDirectory();
        var scanner = new CountingCsProjFileService();
        var command = new RefreshRepositoryProjectsCommand(_git, scanner);

        var response = await command.ExecuteAsync(NewRefreshRequest(
            RepositoryOperationCapabilities.For(calculateVersion: true, discoverProjects: true)));

        Assert.Equal(1, scanner.FindCalls);
        Assert.NotNull(response.Projects);
    }

    [Fact]
    public async Task Project_refresh_from_an_app_without_profiles_keeps_scanning()
    {
        CreateRepositoryDirectory();
        var scanner = new CountingCsProjFileService();
        var command = new RefreshRepositoryProjectsCommand(_git, scanner);

        var response = await command.ExecuteAsync(NewRefreshRequest(capabilities: null));

        Assert.Equal(1, scanner.FindCalls);
        Assert.NotNull(response.Projects);
    }

    private RefreshRepositoryProjectsRequest NewRefreshRequest(RepositoryOperationCapabilities? capabilities) => new()
    {
        WorkspaceRoot = _root,
        WorkspaceName = "ws",
        RepositoryName = "repo",
        Capabilities = capabilities,
    };

    private void CreateRepositoryDirectory()
        => Directory.CreateDirectory(Path.Combine(_git.GetWorkspacePath(_root, "ws"), "repo"));

    /// <summary>
    /// The dispatcher takes one handler per command; only the capability provider matters for warming, so
    /// every handler slot is left empty.
    /// </summary>
    private static CommandDispatcher CreateDispatcher(IWorkspaceCapabilityProvider provider)
    {
        var constructor = typeof(CommandDispatcher).GetConstructors().Single();
        var arguments = constructor.GetParameters()
            .Select(p => p.ParameterType == typeof(IWorkspaceCapabilityProvider) ? (object?)provider : null)
            .ToArray();
        return (CommandDispatcher)constructor.Invoke(arguments);
    }
}
