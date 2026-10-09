using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Git;
using GrayMoon.App.Services.WorkspaceManifest;
using GrayMoon.Application;
using GrayMoon.Application.WorkspaceManifest;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// D8 drift triggers beyond the full Sync: a sync that includes the Workspace-role repository, and a branch or tag
/// checkout of that repository. Drift detection is fire-and-forget, so the tests wait on the probe.
/// </summary>
public sealed class WorkspaceManifestDriftTriggerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NegativeWait = TimeSpan.FromMilliseconds(400);

    private static object SyncResponse() => new
    {
        success = true,
        version = "2.0.0",
        branch = "main",
        defaultBranch = "main",
        outgoingCommits = 0,
        incomingCommits = 0,
        defaultBranchBehind = 0,
        defaultBranchAhead = 0,
        hasUpstream = true,
        upstreamProbed = true,
        localBranches = new[] { "main" },
        remoteBranches = new[] { "origin/main" },
        tags = Array.Empty<string>(),
        projects = Array.Empty<object>(),
    };

    private static Task<SyncStateTestContext> CreateAsync(DriftProbe probe, bool workspaceRole = true) =>
        CreateCoreAsync(probe, workspaceRole);

    private static async Task<SyncStateTestContext> CreateCoreAsync(DriftProbe probe, bool workspaceRole)
    {
        var ctx = await SyncStateTestContext.CreateAsync(
            configureServices: services => services.AddScoped<IWorkspaceManifestService>(_ => probe));
        if (workspaceRole)
            await ctx.MutateLinkAsync(link => link.Role = WorkspaceRepositoryRole.Workspace);
        return ctx;
    }

    [Fact]
    public async Task Single_repository_sync_of_workspace_repository_checks_drift()
    {
        var probe = new DriftProbe();
        await using var ctx = await CreateAsync(probe);
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse());
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();

        await git.SyncAsync(ctx.WorkspaceId, special, repositoryIds: [ctx.RepositoryId]);

        Assert.True(await probe.WaitForCallAsync(Wait));
        Assert.Equal([ctx.WorkspaceId], probe.WorkspaceIds.ToArray());
    }

    [Fact]
    public async Task Checkout_branch_of_workspace_repository_checks_drift()
    {
        var probe = new DriftProbe();
        await using var ctx = await CreateAsync(probe);
        ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = "release/1" });
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();

        var outcome = await ops.CheckoutAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "release/1", isTag: false);

        Assert.True(outcome.IsSuccessStatus);
        Assert.True(await probe.WaitForCallAsync(Wait));
        Assert.Equal([ctx.WorkspaceId], probe.WorkspaceIds.ToArray());
    }

    [Fact]
    public async Task Checkout_tag_of_workspace_repository_checks_drift()
    {
        var probe = new DriftProbe();
        await using var ctx = await CreateAsync(probe);
        ctx.WorkerBridge.Respond("CheckoutTag", new CheckoutTagResponse { Success = true, CurrentTag = "2.0.0" });
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();

        var outcome = await ops.CheckoutAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "2.0.0", isTag: true);

        Assert.True(outcome.IsSuccessStatus);
        Assert.True(await probe.WaitForCallAsync(Wait));
        Assert.Equal([ctx.WorkspaceId], probe.WorkspaceIds.ToArray());
    }

    [Fact]
    public async Task Checkout_of_source_repository_does_not_check_drift()
    {
        var probe = new DriftProbe();
        await using var ctx = await CreateAsync(probe, workspaceRole: false);
        ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = "release/1" });
        ctx.WorkerBridge.Respond("CheckoutTag", new CheckoutTagResponse { Success = true, CurrentTag = "2.0.0" });
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();

        Assert.True((await ops.CheckoutAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "release/1", isTag: false)).IsSuccessStatus);
        Assert.True((await ops.CheckoutAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "2.0.0", isTag: true)).IsSuccessStatus);

        Assert.False(await probe.WaitForCallAsync(NegativeWait));
    }

    [Fact]
    public async Task Drift_check_failure_does_not_fail_the_checkout()
    {
        var probe = new DriftProbe { Throw = true };
        await using var ctx = await CreateAsync(probe);
        ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = "release/1" });
        ctx.WorkerBridge.Respond("CheckoutTag", new CheckoutTagResponse { Success = true, CurrentTag = "2.0.0" });
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();

        var branch = await ops.CheckoutAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "release/1", isTag: false);
        Assert.True(await probe.WaitForCallAsync(Wait));
        var tag = await ops.CheckoutAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "2.0.0", isTag: true);

        Assert.True(branch.IsSuccessStatus);
        Assert.True(tag.IsSuccessStatus);
    }

    /// <summary>Records drift-detection calls; optionally throws to prove the hook is failure-isolated.</summary>
    private sealed class DriftProbe : IWorkspaceManifestService
    {
        private readonly SemaphoreSlim _called = new(0);

        public bool Throw { get; init; }

        public System.Collections.Concurrent.ConcurrentQueue<int> WorkspaceIds { get; } = new();

        public async Task<bool> WaitForCallAsync(TimeSpan timeout) => await _called.WaitAsync(timeout);

        public Task<WorkspaceManifestDrift> DetectDriftAsync(int workspaceId, CancellationToken cancellationToken = default)
        {
            WorkspaceIds.Enqueue(workspaceId);
            _called.Release();
            if (Throw)
                throw new InvalidOperationException("drift detection failed");
            return Task.FromResult(new WorkspaceManifestDrift(false, null, [], [], [], [], []));
        }

        public Task<WorkspaceManifest> BuildFromDatabaseAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string Serialize(WorkspaceManifest manifest) => throw new NotSupportedException();

        public bool TryParse(string content, out WorkspaceManifest? manifest, out string? error) =>
            throw new NotSupportedException();

        public Task<OperationResult> WriteAuthoritativeManifestAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OperationResult> SetRepositoryTagPinsAsync(int workspaceId, IReadOnlyList<WorkspaceRepositoryTagPinChange> changes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OperationResult> WriteManagedGitIgnoreAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}