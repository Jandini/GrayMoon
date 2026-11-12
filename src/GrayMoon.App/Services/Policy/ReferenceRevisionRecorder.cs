using System.Collections.Concurrent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using GrayMoon.Application.WorkspaceManifest;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Policy;

/// <summary>Where one repository is: the branch it is on and the full commit hash of its HEAD.</summary>
public sealed record ReferenceRevision(int RepositoryId, string? Branch, string? Commit);

/// <summary>
/// Records the branch and commit of read-only Source repositories in the Workspace definition, so a restore puts
/// them back where they were. It is called when read-only is turned on and after a pull or a manual switch through
/// GrayMoon - never from a Sync or a hook. Only the special Workspace is recorded, never a Feature.
/// <para>
/// It never throws and never fails its caller: a failed write is logged and returned as a warning, and the next
/// recording event writes the then-current commit. Repositories of one Workspace are pulled in parallel, so the
/// changes that arrive while a write is running are written together by the next one. Uses its own DI scope.
/// </para>
/// </summary>
public sealed class ReferenceRevisionRecorder(IServiceScopeFactory scopeFactory, ILogger<ReferenceRevisionRecorder> logger)
{
    private readonly SyncRecomputeCoalescer _coalescer = new();
    private readonly ConcurrentDictionary<int, ConcurrentQueue<WorkspaceRepositoryReferenceRevisionChange>> _pending = new();

    /// <summary>Records one repository after a pull or a switch in <paramref name="contextId"/>. Does nothing in a Feature.</summary>
    public async Task<string?> RecordAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        int repositoryId,
        string? branch,
        string? commit,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var contextResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();
            if (!(await contextResolver.GetRequiredAsync(contextId, workspaceId, cancellationToken)).IsSpecialWorkspace)
                return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Warn(workspaceId, ex.Message, ex);
        }

        return await RecordAsync(workspaceId, [new ReferenceRevision(repositoryId, branch, commit)], cancellationToken);
    }

    /// <summary>
    /// Records repositories of the special Workspace. Entries that are not read-only Source repositories, or that
    /// are not on a branch (a tag has its own pin), are left out. Returns a warning, or null.
    /// </summary>
    public async Task<string?> RecordAsync(
        int workspaceId,
        IReadOnlyList<ReferenceRevision> revisions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        var onBranch = revisions.Where(revision => !string.IsNullOrWhiteSpace(revision.Branch)).ToList();
        if (onBranch.Count == 0)
            return null;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            // Without a Workspace repository there is no definition to record in.
            if (!await db.WorkspaceRepositories.AnyAsync(
                    link => link.WorkspaceId == workspaceId && link.Role == WorkspaceRepositoryRole.Workspace, cancellationToken))
                return null;

            var ids = onBranch.Select(revision => revision.RepositoryId).ToList();
            var cloneUrls = await db.WorkspaceRepositories
                .AsNoTracking()
                .Where(link => link.WorkspaceId == workspaceId
                    && ids.Contains(link.RepositoryId)
                    && link.Role == WorkspaceRepositoryRole.Source
                    && link.IsReference)
                .Select(link => new { link.RepositoryId, link.Repository!.RepositoryName, link.Repository.CloneUrl })
                .ToDictionaryAsync(row => row.RepositoryId, cancellationToken);

            var unreadable = new List<string>();
            var queue = _pending.GetOrAdd(workspaceId, _ => new ConcurrentQueue<WorkspaceRepositoryReferenceRevisionChange>());
            var queued = 0;
            foreach (var revision in onBranch)
            {
                if (!cloneUrls.TryGetValue(revision.RepositoryId, out var repository))
                    continue;

                if (!WorkspaceDefinitionTagPin.IsFullCommitHash(revision.Commit))
                {
                    unreadable.Add(repository.RepositoryName);
                    continue;
                }

                queue.Enqueue(new WorkspaceRepositoryReferenceRevisionChange(repository.CloneUrl, revision.Branch!.Trim(), revision.Commit));
                queued++;
            }

            if (queued > 0)
                await _coalescer.RunAsync(workspaceId.ToString(System.Globalization.CultureInfo.InvariantCulture), () => WritePendingAsync(workspaceId));

            return unreadable.Count == 0
                ? null
                : Warn(workspaceId, $"the full commit hash of {string.Join(", ", unreadable)} could not be read", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Warn(workspaceId, ex.Message, ex);
        }
    }

    /// <summary>One write for everything queued so far. A later change of the same repository wins.</summary>
    private async Task WritePendingAsync(int workspaceId)
    {
        if (!_pending.TryGetValue(workspaceId, out var queue))
            return;

        var latest = new Dictionary<string, WorkspaceRepositoryReferenceRevisionChange>(StringComparer.OrdinalIgnoreCase);
        while (queue.TryDequeue(out var change))
            latest[RepositoryUrlIdentity.NormalizeRepositoryUrl(change.RepositoryUrl)] = change;
        if (latest.Count == 0)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var manifestService = scope.ServiceProvider.GetRequiredService<IWorkspaceManifestService>();
        // Not the caller's token: other callers share this write.
        var result = await manifestService.SetRepositoryReferenceRevisionsAsync(workspaceId, [.. latest.Values], CancellationToken.None);
        if (!result.Success)
            throw new InvalidOperationException(result.Error ?? "The Workspace definition could not be updated.");
    }

    private string Warn(int workspaceId, string detail, Exception? exception)
    {
        logger.LogWarning(exception, "Recording read only repository revisions failed. WorkspaceId={WorkspaceId} Detail={Detail}", workspaceId, detail);
        return $"The commit of a read only repository could not be recorded in the Workspace definition: {detail.TrimEnd('.')}.";
    }
}
