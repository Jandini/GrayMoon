using System.Collections.Concurrent;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Common.Git;

namespace GrayMoon.Agent.Tests;

/// <summary>Records call counts and concurrency for <see cref="GitStatusRefreshCoordinator"/> tests
/// without touching a real git repository.</summary>
public sealed class FakeRepositoryGitChangesService : IRepositoryGitChangesService
{
    private int _activeCalls;
    private int _maxConcurrentCalls;
    private readonly ConcurrentDictionary<string, int> _activeCallsPerRepo = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _maxConcurrentCallsPerRepo = new(StringComparer.OrdinalIgnoreCase);

    public int CallCount;
    public TimeSpan Delay = TimeSpan.FromMilliseconds(50);

    public int MaxConcurrentCalls => _maxConcurrentCalls;

    public int MaxConcurrentCallsForRepo(string repoPath) =>
        _maxConcurrentCallsPerRepo.GetValueOrDefault(repoPath, 0);

    public async Task<GitChangeStatusResult> GetStatusAsync(string repoPath, long snapshotVersion, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref CallCount);

        var globalActive = Interlocked.Increment(ref _activeCalls);
        InterlockedMax(ref _maxConcurrentCalls, globalActive);

        var repoActive = _activeCallsPerRepo.AddOrUpdate(repoPath, 1, (_, c) => c + 1);
        _maxConcurrentCallsPerRepo.AddOrUpdate(repoPath, repoActive, (_, existing) => Math.Max(existing, repoActive));

        await Task.Delay(Delay, cancellationToken);

        Interlocked.Decrement(ref _activeCalls);
        _activeCallsPerRepo.AddOrUpdate(repoPath, 0, (_, c) => c - 1);

        return new GitChangeStatusResult
        {
            Success = true,
            Snapshot = new GitChangeSnapshot
            {
                Version = snapshotVersion,
                BranchName = "main",
                Changes = [],
                ScannedAt = DateTimeOffset.UtcNow,
            },
        };
    }

    public Task<GitDiffDocument> GetDiffAsync(string repoPath, GitDiffRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not used by these tests.");

    public Task<GitMutationResult> StageAsync(string repoPath, GitStageOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not used by these tests.");

    public Task<GitMutationResult> UnstageAsync(string repoPath, GitStageOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not used by these tests.");

    public Task<GitCommitResult> CommitAsync(string repoPath, GitCommitOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not used by these tests.");

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = target;
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
