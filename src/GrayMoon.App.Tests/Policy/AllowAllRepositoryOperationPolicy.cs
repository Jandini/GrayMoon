using GrayMoon.Application.Features;
using GrayMoon.Application.Repositories.Policy;

namespace GrayMoon.App.Tests.Policy;

/// <summary>
/// A policy that allows everything, for tests of a service that has no database and does not test the policy itself.
/// Tests of what the policy refuses use the real service over a seeded database.
/// </summary>
internal sealed class AllowAllRepositoryOperationPolicy : IWorkspaceRepositoryOperationPolicy
{
    public Task<IReadOnlyDictionary<int, RepositoryPolicySubject>> GetSubjectsAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        RepositorySubjectSource source = RepositorySubjectSource.Context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<int, RepositoryPolicySubject>>(new Dictionary<int, RepositoryPolicySubject>());

    public Task<RepositoryEligibility> PartitionAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        RepositoryOperation operation,
        IReadOnlyCollection<int>? repositoryIds = null,
        RepositorySubjectSource source = RepositorySubjectSource.Context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new RepositoryEligibility(repositoryIds?.Distinct().ToList() ?? [], []));

    public Task<RepositoryOperationDecision> EvaluateAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        RepositoryOperation operation,
        int repositoryId,
        RepositorySubjectSource source = RepositorySubjectSource.Context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(RepositoryOperationDecision.Allowed);
}
