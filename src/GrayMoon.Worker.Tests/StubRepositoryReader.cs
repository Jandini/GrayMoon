using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Tests;

/// <summary>Reader stub for tests that stub git: the default branch is "main"; every other read is unexpected.</summary>
internal sealed class StubRepositoryReader : IGitRepositoryReader
{
    public Task<string?> GetDefaultBranchNameAsync(string repoPath, CancellationToken ct) => Task.FromResult<string?>("main");

    public Task<string?> GetCurrentBranchNameAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
    public Task<string?> GetHeadCommitAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<string>> FindBranchCollisionsAsync(string repoPath, string branchName, CancellationToken ct) => throw new NotImplementedException();
    public Task<string?> RevParseAsync(string repoPath, string rev, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> RefExistsAsync(string repoPath, string revision, CancellationToken ct) => throw new NotImplementedException();
    public Task<string?> GetUpstreamRefAsync(string repoPath, string branchName, CancellationToken ct) => throw new NotImplementedException();
    public Task<string?> GetRemoteOriginUrlAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
    public Task<(int? Outgoing, int? Incoming, bool HasUpstream)> GetCommitCountsAsync(string repoPath, string branchName, string? defaultBranchOriginRef, CancellationToken ct, bool skipUpstreamCheck = false) => throw new NotImplementedException();
    public Task<CommitCountsProbeResult> ProbeCommitCountsAsync(string repoPath, string branchName, string? defaultBranchOriginRef, CancellationToken ct, bool skipUpstreamCheck = false) => throw new NotImplementedException();
    public Task<(int? DefaultBehind, int? DefaultAhead, string? DefaultBranchName)> GetCommitCountsVsDefaultAsync(string repoPath, string? defaultBranchOriginRef, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<string>> GetLocalBranchesAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
    public Task<RefSnapshot?> GetRefSnapshotAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<string>> GetRemoteBranchesFromRefsAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
    public Task<string?> GetDefaultBranchOriginRefAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<string>> GetTagsAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
    public Task<string?> GetCheckedOutTagAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
    public Task<string?> GetDivergenceBaseBranchAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
}
