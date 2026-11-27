using GrayMoon.App.Models;
using GrayMoon.App.Services.Git;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Services.Features;

public sealed partial class WorkspaceFeatureOperations
{
    /// <summary>
    /// Create Feature copies the special Workspace's stored state into the new Feature (projects, dependency
    /// levels, file-version counters, incoming commits). This compares that stored state with the HEAD snapshot
    /// Create has just read from the checkouts, and synchronizes the repositories that differ - a commit, checkout
    /// or reset GrayMoon did not see - before anything is copied. Repositories that agree cost nothing.
    /// <para>
    /// A repository that cannot be synchronized, or whose checkout moved again while it was, stops Create with the
    /// repository named: a Feature is never seeded from state known to be stale.
    /// </para>
    /// </summary>
    private async Task<(int Synchronized, string? Error)> SyncRepositoriesWithStaleStoredStateAsync(
        int workspaceId,
        WorkspaceFeatureContextId specialContextId,
        IReadOnlyList<WorkspaceRepositoryLink> links,
        IReadOnlyDictionary<string, string> liveCommits,
        IReadOnlyDictionary<string, string> liveBranches,
        IReadOnlyDictionary<string, string> liveTags,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stale = await FindRepositoriesWithStaleStoredStateAsync(
            specialContextId, links, liveCommits, liveBranches, liveTags, cancellationToken);
        if (stale.Count == 0)
            return (0, null);

        var names = string.Join(", ", stale.Select(l => l.Repository!.RepositoryName));
        logger.LogInformation(
            "Feature Create: the stored Workspace state differs from the checkout; synchronizing first. WorkspaceId={WorkspaceId} Repositories={Repositories}",
            workspaceId, names);
        progress?.Report(new OperationProgress(
            stale.Count == 1
                ? $"Synchronizing {stale[0].Repository!.RepositoryName}, which changed outside GrayMoon..."
                : $"Synchronizing {stale.Count} repositories that changed outside GrayMoon..."));

        IReadOnlyDictionary<int, RepoGitVersionInfo> results;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            // A subset never persists dependency levels from its partial graph; the Sync recomputes them from
            // the stored edges of the whole Workspace instead (the same call a single-repository Sync makes).
            results = await git.SyncAsync(
                workspaceId,
                specialContextId,
                repositoryIds: stale.Select(l => l.RepositoryId).ToList(),
                skipDependencyLevelPersistence: true,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Feature Create: synchronizing repositories that changed outside GrayMoon failed. WorkspaceId={WorkspaceId} Repositories={Repositories}",
                workspaceId, names);
            return (0, NotCurrentMessage(names, ex.Message));
        }

        foreach (var link in stale)
        {
            var repositoryName = link.Repository!.RepositoryName;
            if (!results.TryGetValue(link.RepositoryId, out var info))
                return (0, NotCurrentMessage(repositoryName, "the synchronization returned no result"));
            if (info.FatalSyncFailure)
                return (0, NotCurrentMessage(repositoryName, info.ErrorMessage ?? "the synchronization failed"));
        }

        // The Sync read the checkouts again. One that no longer matches the snapshot Create holds moved in between.
        var stillStale = await FindRepositoriesWithStaleStoredStateAsync(
            specialContextId, stale, liveCommits, liveBranches, liveTags, cancellationToken, commitOnly: true);
        if (stillStale.Count > 0)
        {
            return (0, NotCurrentMessage(
                string.Join(", ", stillStale.Select(l => l.Repository!.RepositoryName)),
                "the checkout changed again while it was synchronized"));
        }

        return (stale.Count, null);
    }

    /// <summary>
    /// The links whose stored special-Workspace state does not describe the checkout the HEAD snapshot saw: no stored
    /// state or commit, another commit, another branch, or another tag. A detached checkout that is not on a tag is
    /// compared by commit only (the stored branch of such a checkout is whatever its last probe called it).
    /// </summary>
    private async Task<List<WorkspaceRepositoryLink>> FindRepositoriesWithStaleStoredStateAsync(
        WorkspaceFeatureContextId specialContextId,
        IReadOnlyList<WorkspaceRepositoryLink> links,
        IReadOnlyDictionary<string, string> liveCommits,
        IReadOnlyDictionary<string, string> liveBranches,
        IReadOnlyDictionary<string, string> liveTags,
        CancellationToken cancellationToken,
        bool commitOnly = false)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var linkIds = links.Select(l => l.WorkspaceRepositoryId).ToList();
        var stored = await db.WorkspaceRepositoryContextStates
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == specialContextId.Value && linkIds.Contains(s.WorkspaceRepositoryId))
            .Select(s => new { s.WorkspaceRepositoryId, s.HeadCommit, s.BranchName, s.CheckedOutTag })
            .ToDictionaryAsync(s => s.WorkspaceRepositoryId, cancellationToken);

        var stale = new List<WorkspaceRepositoryLink>();
        foreach (var link in links)
        {
            var repositoryName = link.Repository?.RepositoryName;
            if (string.IsNullOrWhiteSpace(repositoryName) || !liveCommits.TryGetValue(repositoryName, out var liveCommit))
                continue;

            if (!stored.TryGetValue(link.WorkspaceRepositoryId, out var state)
                || !SameText(state.HeadCommit, liveCommit, StringComparison.OrdinalIgnoreCase))
            {
                stale.Add(link);
                continue;
            }

            // After the Sync only the commit is held to the snapshot: a probe may name a branch differently than
            // git does, and that must make the next Create synchronize again, not refuse.
            if (commitOnly)
                continue;

            liveBranches.TryGetValue(repositoryName, out var liveBranch);
            liveTags.TryGetValue(repositoryName, out var liveTag);
            var onBranch = !string.IsNullOrWhiteSpace(liveBranch);
            var differs = onBranch
                ? !SameText(state.BranchName, liveBranch, StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(state.CheckedOutTag)
                : !SameText(state.CheckedOutTag, liveTag, StringComparison.OrdinalIgnoreCase);
            if (differs)
                stale.Add(link);
        }

        return stale;
    }

    private static bool SameText(string? stored, string? live, StringComparison comparison)
        => string.Equals(
            string.IsNullOrWhiteSpace(stored) ? null : stored.Trim(),
            string.IsNullOrWhiteSpace(live) ? null : live.Trim(),
            comparison);

    private static string NotCurrentMessage(string repositoryNames, string cause)
        => $"The Feature was not created. {repositoryNames} changed outside GrayMoon and could not be synchronized first: "
            + $"{cause.TrimEnd('.', ' ')}. Synchronize the Workspace and create the Feature again.";
}
