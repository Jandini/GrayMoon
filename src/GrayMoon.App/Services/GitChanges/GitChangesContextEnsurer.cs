using GrayMoon.App.Data;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Renews the Worker-side file watchers of one workspace context without running git: resolves the context's
/// repositories and sends one <c>EnsureGitChangesWatchers</c> batch. Timeouts and scheduling belong to the caller.
/// </summary>
public interface IGitChangesContextEnsurer
{
    Task<GitChangesEnsureResult> EnsureAsync(int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken);
}

public sealed class GitChangesContextEnsurer(IServiceScopeFactory scopeFactory) : IGitChangesContextEnsurer
{
    public async Task<GitChangesEnsureResult> EnsureAsync(
        int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var targets = await GitChangesMonitorTargets.ResolveAsync(
            services.GetRequiredService<AppDbContext>(),
            services.GetRequiredService<IWorkspaceContextPathResolver>(),
            workspaceId, contextId, null, cancellationToken);
        if (targets.Count == 0)
        {
            return new GitChangesEnsureResult { Success = true };
        }

        var first = targets[0];
        return await services.GetRequiredService<IGitChangesWorkerClient>().EnsureWatchersAsync(
            first.Root, first.WorkspaceName, first.WorkspaceRepositoryName, workspaceId,
            targets.Select(t => new GitChangesEnsureRepository(t.RepositoryId, t.RepositoryName)).ToList(),
            cancellationToken);
    }
}
