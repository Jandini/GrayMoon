using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// <see cref="FeatureFolderCleanupService"/>: the silent background sweep of Feature folders left pending deletion. It
/// sends the Workspace's managed storage root with every existing Feature excluded, is throttled per Workspace, and
/// skips a Workspace that has no storage root or is busy with Create / Remove Feature.
/// </summary>
public sealed class FeatureFolderCleanupServiceTests
{
    private const string StorageRoot = @"C:\Users\test\.graymoon\test-ws\features";

    [Fact]
    public async Task Sweep_sends_the_storage_root_and_excludes_every_existing_Feature()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedAsync(ctx, StorageRoot, "live", "team/other");
        ctx.WorkerBridge.Respond(WorkerHubMethods.SweepPendingFeatureFolders, new { removed = 1, stillPending = 0, refused = 0 });
        await using var scope = ctx.CreateScope();
        var service = NewService(scope);

        var swept = await service.SweepWorkspaceAsync(ctx.WorkspaceId, CancellationToken.None);

        Assert.True(swept);
        var call = Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.SweepPendingFeatureFolders);
        Assert.Equal(StorageRoot, GetArg(call.Args, "featureStorageRoot"));
        Assert.Equal("test-ws", GetArg(call.Args, "workspaceName"));
        var excluded = Assert.IsAssignableFrom<IEnumerable<string>>(GetArg(call.Args, "excludeFeatureNames"));
        Assert.Equal(["live", "team/other"], excluded.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_second_sweep_within_the_interval_is_skipped()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedAsync(ctx, StorageRoot);
        ctx.WorkerBridge.Respond(WorkerHubMethods.SweepPendingFeatureFolders, new { removed = 0, stillPending = 0, refused = 0 });
        await using var scope = ctx.CreateScope();
        var service = NewService(scope);

        Assert.True(await service.SweepWorkspaceAsync(ctx.WorkspaceId, CancellationToken.None));
        Assert.False(await service.SweepWorkspaceAsync(ctx.WorkspaceId, CancellationToken.None));

        service.MinInterval = TimeSpan.Zero;
        Assert.True(await service.SweepWorkspaceAsync(ctx.WorkspaceId, CancellationToken.None));
        Assert.Equal(2, ctx.WorkerBridge.Calls.Count(c => c.Command == WorkerHubMethods.SweepPendingFeatureFolders));
    }

    [Fact]
    public async Task No_managed_storage_root_or_the_legacy_drive_root_sends_nothing()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var service = NewService(scope);
        service.MinInterval = TimeSpan.Zero;

        Assert.False(await service.SweepWorkspaceAsync(ctx.WorkspaceId, CancellationToken.None));
        await SeedAsync(ctx, @"C:\.graymoon\test-ws\features");
        Assert.False(await service.SweepWorkspaceAsync(ctx.WorkspaceId, CancellationToken.None));

        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.SweepPendingFeatureFolders);
    }

    [Fact]
    public async Task No_Worker_means_no_sweep_and_the_next_request_can_try_again()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedAsync(ctx, StorageRoot);
        ctx.WorkerBridge.Respond(WorkerHubMethods.SweepPendingFeatureFolders, new { removed = 0, stillPending = 0, refused = 0 });
        await using var scope = ctx.CreateScope();
        var service = NewService(scope);

        ctx.WorkerBridge.IsWorkerConnected = false;
        Assert.False(await service.SweepWorkspaceAsync(ctx.WorkspaceId, CancellationToken.None));

        ctx.WorkerBridge.IsWorkerConnected = true;
        Assert.True(await service.SweepWorkspaceAsync(ctx.WorkspaceId, CancellationToken.None));
    }

    [Fact]
    public async Task A_Workspace_busy_with_a_structural_operation_is_skipped()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedAsync(ctx, StorageRoot);
        await using var scope = ctx.CreateScope();
        var service = NewService(scope);
        var operationLock = scope.ServiceProvider.GetRequiredService<IWorkspaceOperationLock>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(operationLock.TryStartStructural(
            ctx.WorkspaceId, "remove-feature", "overlay", "Removing feature...", (_, _) => release.Task, out var operation));

        try
        {
            Assert.False(await service.SweepWorkspaceAsync(ctx.WorkspaceId, CancellationToken.None));
            Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.SweepPendingFeatureFolders);
        }
        finally
        {
            release.SetResult();
            await operation.WhenCompleted;
        }
    }

    private static FeatureFolderCleanupService NewService(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
        scope.ServiceProvider.GetRequiredService<WorkerConnectionTracker>(),
        scope.ServiceProvider.GetRequiredService<IWorkspaceOperationLock>(),
        NullLogger<FeatureFolderCleanupService>.Instance);

    private static object? GetArg(object args, string name) => args.GetType().GetProperty(name)?.GetValue(args);

    private static async Task SeedAsync(SyncStateTestContext ctx, string storageRoot, params string[] featureNames)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var workspace = await db.Workspaces.SingleAsync(w => w.WorkspaceId == ctx.WorkspaceId);
        workspace.ManagedFeatureStorageRoot = storageRoot;
        foreach (var name in featureNames)
        {
            db.WorkspaceFeatures.Add(new WorkspaceFeature
            {
                WorkspaceId = ctx.WorkspaceId,
                Name = name,
                LifecycleState = WorkspaceFeatureLifecycleState.Ready,
                BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }
}
