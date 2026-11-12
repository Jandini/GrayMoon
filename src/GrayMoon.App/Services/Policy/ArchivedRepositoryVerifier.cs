using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Services.GitHub;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Policy;

/// <summary>Asks the provider whether one repository is archived right now.</summary>
public interface IRepositoryArchiveProbe
{
    /// <summary>True or false as the provider answers; null when it cannot be asked or does not answer.</summary>
    Task<bool?> IsArchivedAsync(Repository repository, CancellationToken cancellationToken = default);
}

/// <summary>The probe for GitHub: one conditional request for the repository.</summary>
public sealed class GitHubRepositoryArchiveProbe(GitHubService gitHubService) : IRepositoryArchiveProbe
{
    public async Task<bool?> IsArchivedAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        var connector = repository.Connector;
        if (connector is null || connector.IsRemoved || connector.ConnectorType != ConnectorType.GitHub)
            return null;
        if (!RepositoryUrlHelper.TryParseGitHubOwnerRepo(repository.CloneUrl, out var owner, out var name) || owner is null || name is null)
            return null;

        var settings = await gitHubService.GetRepositoryMergeSettingsAsync(connector, owner, name, cancellationToken);
        return settings?.Archived;
    }
}

/// <summary>
/// GrayMoon learns that a repository is archived only when repositories are fetched, and it does not push an archived
/// repository. So a repository that was archived and is not any more would stay locked until the next fetch. This
/// asks the provider again, only for the repositories of one Workspace that are marked archived, and clears the mark
/// of each one the provider reports as not archived. It never sets the mark and never fails its caller.
/// </summary>
public sealed class ArchivedRepositoryVerifier(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IRepositoryArchiveProbe probe,
    IHubContext<WorkspaceSyncHub> hubContext,
    ILogger<ArchivedRepositoryVerifier> logger)
{
    /// <summary>Names of the repositories that are not archived any more. Empty when nothing changed.</summary>
    public async Task<IReadOnlyList<string>> VerifyAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var cleared = new List<string>();
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var archived = await db.Repositories
                .AsNoTracking()
                .Include(repository => repository.Connector)
                .Where(repository => repository.Archived
                    && db.WorkspaceRepositories.Any(link => link.WorkspaceId == workspaceId && link.RepositoryId == repository.RepositoryId))
                .OrderBy(repository => repository.RepositoryName)
                .ToListAsync(cancellationToken);

            foreach (var repository in archived)
            {
                bool? stillArchived;
                try
                {
                    stillArchived = await probe.IsArchivedAsync(repository, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Archived check failed for repository {RepositoryId}", repository.RepositoryId);
                    continue;
                }

                // No answer keeps the mark: an archived repository must not look writable because the provider was unreachable.
                if (stillArchived != false)
                    continue;

                await db.Repositories
                    .Where(row => row.RepositoryId == repository.RepositoryId)
                    .ExecuteUpdateAsync(s => s.SetProperty(row => row.Archived, false), cancellationToken);
                cleared.Add(repository.RepositoryName);
            }

            if (cleared.Count > 0)
            {
                logger.LogInformation(
                    "Workspace {WorkspaceId}: no longer archived on the provider: {Repositories}",
                    workspaceId, string.Join(", ", cleared));
                await hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Archived check failed for workspace {WorkspaceId}", workspaceId);
        }

        return cleared;
    }
}
