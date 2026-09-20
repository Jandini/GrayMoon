using GrayMoon.App.Data;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Features;

public sealed class WorkspaceNativeLaunchService(
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceFeatureContextResolver contextResolver,
    IDbContextFactory<AppDbContext> dbContextFactory) : IWorkspaceNativeLaunchService
{
    public async Task<NativeLaunchPaths?> ResolveContextLaunchPathsAsync(
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
    {
        var info = await contextResolver.GetRequiredAsync(contextId, cancellationToken: cancellationToken);
        var root = await pathResolver.GetContextRootAsync(contextId, cancellationToken);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var links = await db.WorkspaceRepositories
            .AsNoTracking()
            .Include(l => l.Repository)
            .Where(l => l.WorkspaceId == info.WorkspaceId)
            .ToListAsync(cancellationToken);

        var repos = new List<NativeLaunchRepositoryPath>();
        foreach (var link in links)
        {
            if (link.Repository is null)
                continue;
            var path = await pathResolver.GetRepositoryPathAsync(contextId, link.WorkspaceRepositoryId, cancellationToken);
            repos.Add(new NativeLaunchRepositoryPath
            {
                WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                RepositoryName = link.Repository.RepositoryName,
                Path = path
            });
        }

        return new NativeLaunchPaths { ContextRoot = root, Repositories = repos };
    }
}
