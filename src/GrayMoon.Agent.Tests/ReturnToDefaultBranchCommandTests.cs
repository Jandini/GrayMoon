using GrayMoon.Abstractions.Notifications;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Commands;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Agent.Tests;

public sealed class ReturnToDefaultBranchCommandTests
{
    [Fact]
    public async Task ExecuteAsync_PassesBearerToken_ToRemoteBranchDelete()
    {
        var git = new RecordingGitService();
        var probe = new StubRepositoryStateProbe();
        var command = new ReturnToDefaultBranchCommand(git, probe, NullLogger<ReturnToDefaultBranchCommand>.Instance);

        var response = await command.ExecuteAsync(new ReturnToDefaultBranchRequest
        {
            WorkspaceRoot = @"C:\workspaces",
            WorkspaceName = "ws",
            RepositoryName = "repo",
            CurrentBranchName = "matt",
            BearerToken = "connector-token",
            DeleteRemoteBranch = true,
            ForceDeleteLocalBranch = false,
        });

        Assert.True(response.Success);

        var remoteDelete = Assert.Single(git.DeleteCalls, c => c.IsRemote);
        Assert.Equal("connector-token", remoteDelete.BearerToken);
        Assert.True(remoteDelete.SkipHooks);
        Assert.Equal("matt", remoteDelete.BranchName);
    }

    private sealed class StubRepositoryStateProbe : IRepositoryStateProbe
    {
        public Task<RepositoryStateCapture> CaptureAsync(string repoPath, RepositoryStateProbeOptions options, CancellationToken ct = default)
            => Task.FromResult(new RepositoryStateCapture(new RepositoryStateSnapshot { BranchName = "main", DefaultBranchName = "main" }, null));
    }

    /// <summary>Minimal IGitService stub that records DeleteBranchAsync calls for the return-to-default path.</summary>
    private sealed class RecordingGitService : IGitService
    {
        public List<DeleteCall> DeleteCalls { get; } = [];

        public sealed record DeleteCall(string BranchName, bool IsRemote, bool Force, bool SkipHooks, string? BearerToken);

        public string GetWorkspacePath(string root, string workspaceName) => Path.Combine(root, workspaceName);
        public bool DirectoryExists(string path) => true;
        public Task<string?> GetDefaultBranchNameAsync(string repoPath, CancellationToken ct) => Task.FromResult<string?>("main");

        public Task<(bool Success, string? ErrorMessage)> DeleteBranchAsync(
            string repoPath, string branchName, bool isRemote, bool force, CancellationToken ct, bool skipHooks = false, string? bearerToken = null)
        {
            DeleteCalls.Add(new DeleteCall(branchName, isRemote, force, skipHooks, bearerToken));
            return Task.FromResult<(bool, string?)>((true, null));
        }

        public Task<(bool Success, string? ErrorMessage)> FetchAsync(string repoPath, bool includeTags, string? bearerToken, CancellationToken ct)
            => Task.FromResult<(bool, string?)>((true, null));

        public Task<(bool Success, string? ErrorMessage)> CheckoutBranchAsync(string repoPath, string branchName, CancellationToken ct, bool skipHooks = false)
            => Task.FromResult<(bool, string?)>((true, null));

        public Task<(bool Success, bool MergeConflict, string? ErrorMessage)> PullAsync(string repoPath, string branchName, string? bearerToken, CancellationToken ct, bool skipHooks = false)
            => Task.FromResult<(bool, bool, string?)>((true, false, null));

        public Task<bool> CloneAsync(string workingDir, string cloneUrl, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
        public Task AddSafeDirectoryAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
        public Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
        public Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, bool nonNormalize, CancellationToken ct) => throw new NotImplementedException();
        public Task<string?> GetCurrentBranchNameAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
        public Task<string?> GetHeadCommitAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
        public Task<string?> GetRemoteOriginUrlAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, string? ErrorMessage)> FetchMinimalAsync(string repoPath, string branchName, string? defaultBranchOriginRef, string? bearerToken, CancellationToken ct, bool skipUpstreamCheck = false) => throw new NotImplementedException();
        public Task<(int? Outgoing, int? Incoming, bool HasUpstream)> GetCommitCountsAsync(string repoPath, string branchName, string? defaultBranchOriginRef, CancellationToken ct, bool skipUpstreamCheck = false) => throw new NotImplementedException();
        public Task<CommitCountsProbeResult> ProbeCommitCountsAsync(string repoPath, string branchName, string? defaultBranchOriginRef, CancellationToken ct, bool skipUpstreamCheck = false) => throw new NotImplementedException();
        public Task<(int? DefaultBehind, int? DefaultAhead, string? DefaultBranchName)> GetCommitCountsVsDefaultAsync(string repoPath, string? defaultBranchOriginRef, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, string? ErrorMessage)> PushAsync(string repoPath, string branchName, string? bearerToken, bool setTracking = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AbortMergeAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, bool HasConflicts, IReadOnlyList<string> ConflictFiles, string? ErrorMessage)> MergeFromRemoteAsync(string repoPath, string remoteBranch, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetLocalBranchesAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetRemoteBranchesFromRefsAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetRemoteBranchesAsync(string repoPath, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, string? ErrorMessage)> CreateBranchAsync(string repoPath, string newBranchName, string baseBranchName, CancellationToken ct, bool skipHooks = false) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetTagsAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, string? ErrorMessage)> FetchTagsAsync(string repoPath, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, string? ErrorMessage)> CheckoutTagAsync(string repoPath, string tagName, CancellationToken ct) => throw new NotImplementedException();
        public Task<string?> GetCheckedOutTagAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
        public Task<string?> GetDefaultBranchOriginRefAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, bool Committed, string? ErrorMessage)> StageAndCommitAsync(string repoPath, IReadOnlyList<string> pathsToStage, string commitMessage, CancellationToken ct, bool skipHooks = false) => throw new NotImplementedException();
        public Task<(bool Success, string? ErrorMessage)> ResetToRemoteAsync(string repoPath, string branchName, bool keepChanges, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
        public void CreateDirectory(string path) => throw new NotImplementedException();
        public string[] GetDirectories(string path) => throw new NotImplementedException();
        public void WriteSyncHooks(string repoPath, int workspaceId, int repositoryId) => throw new NotImplementedException();
    }
}
