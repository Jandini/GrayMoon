using GrayMoon.Abstractions.Notifications;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

public sealed class ReturnToDefaultBranchCommandTests
{
    [Fact]
    public async Task ExecuteAsync_PassesBearerToken_ToRemoteBranchDelete()
    {
        using var workspace = new TempWorkspaceRoot("ws", "repo");
        var git = new RecordingGitService();
        var probe = new StubRepositoryStateProbe();
        var command = new ReturnToDefaultBranchCommand(git, new StubRepositoryReader(), probe, NullLogger<ReturnToDefaultBranchCommand>.Instance);

        var response = await command.ExecuteAsync(new ReturnToDefaultBranchRequest
        {
            WorkspaceRoot = workspace.Path,
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


        public Task<(bool Success, string? ErrorMessage)> DeleteBranchAsync(
            string repoPath, string branchName, bool isRemote, bool force, CancellationToken ct, bool skipHooks = false, string? bearerToken = null, string? expectedSha = null)
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
        public Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, bool nonNormalize, string? commitSha, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, string? ErrorMessage)> FetchMinimalAsync(string repoPath, string branchName, string? defaultBranchOriginRef, string? bearerToken, CancellationToken ct, bool skipUpstreamCheck = false) => throw new NotImplementedException();
        public Task<(bool Success, string? ErrorMessage)> PushAsync(string repoPath, string branchName, string? bearerToken, bool setTracking = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AbortMergeAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, bool HasConflicts, IReadOnlyList<string> ConflictFiles, string? ErrorMessage)> MergeFromRemoteAsync(string repoPath, string remoteBranch, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetRemoteBranchesAsync(string repoPath, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, string? ErrorMessage)> CreateBranchAsync(string repoPath, string newBranchName, string baseBranchName, CancellationToken ct, bool skipHooks = false) => throw new NotImplementedException();
        public Task<(bool Success, string? ErrorMessage)> FetchTagsAsync(string repoPath, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, string? ErrorMessage)> CheckoutTagAsync(string repoPath, string tagName, CancellationToken ct) => throw new NotImplementedException();
        public Task SetDivergenceBaseBranchAsync(string repoPath, string? divergenceBaseBranch, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, bool Committed, string? ErrorMessage)> StageAndCommitAsync(string repoPath, IReadOnlyList<string> pathsToStage, string commitMessage, CancellationToken ct, bool skipHooks = false) => throw new NotImplementedException();
        public Task<bool> CloneIntoAsync(string targetDir, string cloneUrl, string? bearerToken, CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> InitAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> AddRemoteAsync(string repoPath, string name, string url, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetRemoteDefaultBranchAsync(string repoPath, string? bearerToken, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> RepairOriginHeadAsync(string repoPath, string? bearerToken, CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> CheckoutTrackingAsync(string repoPath, string branch, CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> SetUnbornHeadAsync(string repoPath, string branch, CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Success, string? ErrorMessage)> ResetToRemoteAsync(string repoPath, string branchName, bool keepChanges, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
        public Task WriteSyncHooksAsync(string repoPath, int workspaceId, int repositoryId, CancellationToken ct) => throw new NotImplementedException();
    }
}
