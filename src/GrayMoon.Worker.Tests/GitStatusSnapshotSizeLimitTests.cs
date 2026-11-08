using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services.GitChanges;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

public sealed class GitStatusSnapshotSizeLimitTests
{
    private sealed class BigStatusService(int changes) : IRepositoryGitChangesService
    {
        public Task<GitChangeStatusResult> GetStatusAsync(string repoPath, long snapshotVersion, CancellationToken cancellationToken, bool includeLineStats = false)
            => Task.FromResult(new GitChangeStatusResult
            {
                Success = true,
                Snapshot = new GitChangeSnapshot
                {
                    Version = snapshotVersion,
                    BranchName = "main",
                    Changes = Enumerable.Range(0, changes)
                        .Select(i => new GitChangeEntry { Path = $"f{i}", WorktreeChange = GitChangeKind.Deleted })
                        .ToList(),
                    ScannedAt = DateTimeOffset.UtcNow,
                },
            });

        public Task<GitDiffDocument> GetDiffAsync(string repoPath, GitDiffRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GitMutationResult> StageAsync(string repoPath, GitStageOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GitMutationResult> UnstageAsync(string repoPath, GitStageOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GitMutationResult> DiscardAsync(string repoPath, GitStageOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GitCommitResult> CommitAsync(string repoPath, GitCommitOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static GitStatusRefreshCoordinator Create(int changes, int limit)
        => new(new BigStatusService(changes), new GitChangesSnapshotCache(),
            Options.Create(new GitChangesOptions { MaxChangesPerSnapshot = limit }),
            NullLogger<GitStatusRefreshCoordinator>.Instance);

    [Fact]
    public async Task A_scan_above_the_limit_is_a_TooManyChanges_failure_and_is_not_published()
    {
        using var coordinator = Create(changes: 50, limit: 10);
        var published = 0;
        coordinator.SnapshotReady += (_, _) => Interlocked.Increment(ref published);

        var result = await coordinator.RefreshNowAsync(@"C:\repo", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TooManyChanges", result.ErrorCode);
        Assert.Null(result.Snapshot);
        Assert.Equal(0, published);
    }

    [Fact]
    public async Task A_scan_at_or_below_the_limit_is_published_normally()
    {
        using var coordinator = Create(changes: 10, limit: 10);
        var published = 0;
        coordinator.SnapshotReady += (_, _) => Interlocked.Increment(ref published);

        var result = await coordinator.RefreshNowAsync(@"C:\repo", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, published);
    }
}
