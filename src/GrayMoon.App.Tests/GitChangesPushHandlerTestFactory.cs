using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

internal static class GitChangesPushHandlerTestFactory
{
    public static GitChangesSnapshotPushHandler Create(
        GitChangesTestDbContext ctx,
        FakeHubContext<WorkspaceSyncHub> hubContext,
        IGitChangesLineStatsRefresh? refresh = null)
    {
        var factory = new GitChangesTestDbContext.TestDbContextFactory(ctx.Options);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<AppDbContext>>(factory);
        services.AddScoped<IWorkspaceFeatureContextResolver, WorkspaceFeatureContextResolver>();
        services.AddScoped<IWorkspaceHookContextAttributor, AlwaysSpecialWorkspaceAttributor>();
        var sp = services.BuildServiceProvider();
        var scope = sp.CreateScope();
        return new GitChangesSnapshotPushHandler(
            factory,
            hubContext,
            NullLogger<GitChangesSnapshotPushHandler>.Instance,
            refresh ?? new NoopGitChangesLineStatsRefresh(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceHookContextAttributor>(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>());
    }

    /// <summary>
    /// Test double: always attributes to the special Workspace context (legacy null-path behavior).
    /// </summary>
    private sealed class AlwaysSpecialWorkspaceAttributor(IWorkspaceFeatureContextResolver contextResolver)
        : IWorkspaceHookContextAttributor
    {
        public async Task<WorkspaceFeatureContextId?> ResolveAsync(
            int workspaceId,
            int repositoryId,
            string? repositoryPath,
            int? claimedContextId = null,
            CancellationToken cancellationToken = default)
            => await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
    }
}
