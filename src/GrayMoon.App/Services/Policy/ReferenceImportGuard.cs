using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Policy;

/// <summary>
/// Source repositories added to a Workspace start with the Workspace's read-only default. A repository added from a
/// folder that already exists may hold commits that are only on this machine, so those that start read-only go
/// through the activation check: one that is blocked is made writable again and reported. Nothing else is changed.
/// </summary>
public sealed class ReferenceImportGuard(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ReferenceActivationChecker activationChecker,
    ILogger<ReferenceImportGuard> logger)
{
    /// <summary>
    /// Checks the read-only links among <paramref name="repositoryIds"/> and returns one message per repository that
    /// was made writable. Empty when none of them is read-only.
    /// </summary>
    public async Task<IReadOnlyList<string>> ApplyAsync(
        int workspaceId,
        IReadOnlyCollection<int> repositoryIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryIds);
        if (repositoryIds.Count == 0)
            return [];

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var referenceIds = await db.WorkspaceRepositories
            .AsNoTracking()
            .Where(link => link.WorkspaceId == workspaceId
                && repositoryIds.Contains(link.RepositoryId)
                && link.Role != WorkspaceRepositoryRole.Workspace
                && link.IsReference)
            .Select(link => link.RepositoryId)
            .ToListAsync(cancellationToken);
        if (referenceIds.Count == 0)
            return [];

        var report = await activationChecker.CheckAsync(workspaceId, referenceIds, cancellationToken);
        var blocked = report.Blocks
            .GroupBy(finding => finding.RepositoryId)
            .OrderBy(group => group.First().RepositoryName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (blocked.Count == 0)
            return [];

        var blockedIds = blocked.Select(group => group.Key).ToList();
        var links = await db.WorkspaceRepositories
            .Where(link => link.WorkspaceId == workspaceId && blockedIds.Contains(link.RepositoryId))
            .ToListAsync(cancellationToken);
        foreach (var link in links)
            link.IsReference = false;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Persistence: saved WorkspaceRepository links. Action=ImportKeptWritable, WorkspaceId={WorkspaceId}, RepositoryIds=[{RepositoryIds}]",
            workspaceId, string.Join(", ", blockedIds));

        return [.. blocked.Select(group =>
            $"{string.Join(" ", group.Select(finding => finding.Message))} It was added as writable, not read-only.")];
    }
}
