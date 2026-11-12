using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using GrayMoon.Application.Repositories.Policy;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Policy;

/// <summary>
/// Reads each repository's checkout state and reference flags for a context and applies <see cref="RepositoryOperationPolicy"/>.
/// The single reader of "which row holds this context's tag". Uses factory-created contexts only, so it is safe
/// beside a circuit's DbContext and inside parallel work.
/// </summary>
public sealed class WorkspaceRepositoryOperationPolicyService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ILogger<WorkspaceRepositoryOperationPolicyService> logger) : IWorkspaceRepositoryOperationPolicy
{
    public async Task<IReadOnlyDictionary<int, RepositoryPolicySubject>> GetSubjectsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        RepositorySubjectSource source = RepositorySubjectSource.Context,
        CancellationToken cancellationToken = default) =>
        (await LoadAsync(workspaceId, contextId, source, cancellationToken))
            .ToDictionary(row => row.RepositoryId, row => row.Subject);

    public async Task<RepositoryEligibility> PartitionAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        RepositoryOperation operation,
        IReadOnlyCollection<int>? repositoryIds = null,
        RepositorySubjectSource source = RepositorySubjectSource.Context,
        CancellationToken cancellationToken = default)
    {
        var rows = await LoadAsync(workspaceId, contextId, source, cancellationToken);
        IEnumerable<SubjectRow> candidates = rows;
        if (repositoryIds != null)
        {
            var byId = rows.ToDictionary(row => row.RepositoryId);
            candidates = repositoryIds.Distinct().Where(byId.ContainsKey).Select(id => byId[id]);
        }

        var eligible = new List<int>();
        var skipped = new List<RepositorySkip>();
        foreach (var row in candidates)
        {
            var decision = RepositoryOperationPolicy.Evaluate(operation, RepositoryOperationScope.Bulk, row.Subject);
            if (decision.IsAllowed)
                eligible.Add(row.RepositoryId);
            else
                skipped.Add(new RepositorySkip(row.RepositoryId, row.RepositoryName, decision.Reason, decision.Message ?? string.Empty));
        }

        if (skipped.Count > 0)
        {
            logger.LogDebug(
                "{Operation} leaves out {Count} repositories. WorkspaceId={WorkspaceId}, ContextId={ContextId}, Skipped={Skipped}",
                operation,
                skipped.Count,
                workspaceId,
                contextId.Value,
                string.Join(", ", skipped.Select(skip => $"{skip.RepositoryName} ({skip.Reason})")));
        }

        return new RepositoryEligibility(eligible, skipped);
    }

    public async Task<RepositoryOperationDecision> EvaluateAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        RepositoryOperation operation,
        int repositoryId,
        RepositorySubjectSource source = RepositorySubjectSource.Context,
        CancellationToken cancellationToken = default)
    {
        var row = (await LoadAsync(workspaceId, contextId, source, cancellationToken, repositoryId)).FirstOrDefault();
        return row is null
            ? RepositoryOperationDecision.Allowed
            : RepositoryOperationPolicy.Evaluate(operation, RepositoryOperationScope.Individual, row.Subject);
    }

    private sealed record SubjectRow(int RepositoryId, string RepositoryName, RepositoryPolicySubject Subject);

    private async Task<List<SubjectRow>> LoadAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        RepositorySubjectSource source,
        CancellationToken cancellationToken,
        int? repositoryId = null)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var linkQuery = db.WorkspaceRepositories.AsNoTracking().Where(link => link.WorkspaceId == workspaceId);
        if (repositoryId is { } id)
            linkQuery = linkQuery.Where(link => link.RepositoryId == id);
        var links = await linkQuery
            .OrderBy(link => link.RepositoryId)
            .Select(link => new
            {
                link.RepositoryId,
                link.WorkspaceRepositoryId,
                RepositoryName = link.Repository != null ? link.Repository.RepositoryName : null,
                link.Role,
                link.IsReference,
                link.BranchName,
                link.CheckedOutTag,
                Archived = link.Repository != null && link.Repository.Archived,
            })
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
            return [];

        // A context that does not exist is not the Workspace: it reads state rows and finds none.
        var isSpecialWorkspace = await db.WorkspaceFeatureContexts
            .AsNoTracking()
            .Where(context => context.WorkspaceFeatureContextId == contextId.Value)
            .Select(context => context.Kind == WorkspaceFeatureContextKind.Workspace)
            .FirstOrDefaultAsync(cancellationToken);

        var readsStates = source == RepositorySubjectSource.ContextStateFirst
            || (source == RepositorySubjectSource.Context && !isSpecialWorkspace);
        var states = new Dictionary<int, (string? BranchName, string? CheckedOutTag)>();
        if (readsStates)
        {
            var linkIds = links.Select(link => link.WorkspaceRepositoryId).ToList();
            states = (await db.WorkspaceRepositoryContextStates
                    .AsNoTracking()
                    .Where(state => state.WorkspaceFeatureContextId == contextId.Value && linkIds.Contains(state.WorkspaceRepositoryId))
                    .Select(state => new { state.WorkspaceRepositoryId, state.BranchName, state.CheckedOutTag })
                    .ToListAsync(cancellationToken))
                .ToDictionary(state => state.WorkspaceRepositoryId, state => (state.BranchName, state.CheckedOutTag));
        }

        // A reference snapshot belongs to one Feature; the Workspace itself never has one.
        var snapshotLinkIds = new HashSet<int>();
        if (!isSpecialWorkspace)
        {
            var allLinkIds = links.Select(link => link.WorkspaceRepositoryId).ToList();
            snapshotLinkIds = (await db.WorkspaceFeatureRepositories
                    .AsNoTracking()
                    .Where(row => row.WorkspaceFeatureContextId == contextId.Value
                        && row.IsReferenceSnapshot
                        && allLinkIds.Contains(row.WorkspaceRepositoryId))
                    .Select(row => row.WorkspaceRepositoryId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
        }

        return links.Select(link =>
        {
            string? branchName;
            string? checkedOutTag;
            if (readsStates && states.TryGetValue(link.WorkspaceRepositoryId, out var state))
                (branchName, checkedOutTag) = state;
            else if (isSpecialWorkspace)
                (branchName, checkedOutTag) = (link.BranchName, link.CheckedOutTag);
            else
                (branchName, checkedOutTag) = (null, null);

            return new SubjectRow(
                link.RepositoryId,
                link.RepositoryName ?? string.Empty,
                RepositoryPolicySubject.ForCheckout(
                    branchName,
                    checkedOutTag,
                    isWorkspaceRepository: link.Role == WorkspaceRepositoryRole.Workspace,
                    isFeatureContext: !isSpecialWorkspace,
                    isReference: link.IsReference,
                    isReferenceSnapshot: snapshotLinkIds.Contains(link.WorkspaceRepositoryId),
                    isArchived: link.Archived));
        }).ToList();
    }
}
