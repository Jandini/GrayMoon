using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

public sealed class AttachWorkspaceRepositoryCloneFailureTests : IDisposable
{
    private const string Secret = "SSOQUERYTOKEN";
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-attach-clone-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Failed_clone_into_reports_the_safe_message_and_removes_only_git_residue()
    {
        var git = new CloneIntoGit(workspace =>
        {
            Directory.CreateDirectory(Path.Combine(workspace, ".git"));
            return GitCloneResult.FromFailure(new GitRemoteFailure(
                128,
                GitRemoteFailureKind.SsoAuthorizationRequired,
                "GitHub SSO authorization required. Reauthorize Visual Studio to access acme-labs. Complete authorization in your browser, then retry cloning.",
                "https://github.com/enterprises/acme-labs-global/sso?authorization_request=" + Secret,
                "Visual Studio",
                "acme-labs"));
        });
        var command = new AttachWorkspaceRepositoryCommand(git, null!);

        var response = await command.ExecuteAsync(new AttachWorkspaceRepositoryRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            CloneUrl = "https://github.com/acme/ws.git",
            WorkspaceId = 1,
            RepositoryId = 1,
        });

        var path = Path.Combine(_root, "ws");
        Assert.False(response.Success);
        Assert.Contains("GitHub SSO authorization required", response.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Visual Studio", response.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, response.ErrorMessage, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(path, ".git")));
    }

    [Fact]
    public async Task Existing_files_in_a_non_empty_root_are_not_deleted_when_clone_is_not_used()
    {
        var path = Path.Combine(_root, "ws");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "notes.txt"), "keep me\n");
        var git = new CloneIntoGit(_ => throw new InvalidOperationException("clone should not run"));
        var command = new AttachWorkspaceRepositoryCommand(git, null!);

        var response = await command.ExecuteAsync(new AttachWorkspaceRepositoryRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            CloneUrl = "https://github.com/acme/ws.git",
            WorkspaceId = 1,
            RepositoryId = 1,
        });

        // Init fails on this stub, so a non-empty root stops there. The notes stay.
        Assert.False(response.Success);
        Assert.Equal("keep me\n", File.ReadAllText(Path.Combine(path, "notes.txt")));
    }

    private sealed class CloneIntoGit(Func<string, GitCloneResult> clone) : GrayMoon.Worker.Abstractions.IGitService
    {
        public Task<GitCloneResult> CloneIntoAsync(string targetDir, string cloneUrl, string? bearerToken, CancellationToken ct)
            => Task.FromResult(clone(targetDir));

        public Task<GitCloneResult> CloneAsync(string workingDir, string cloneUrl, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
    public Task AddSafeDirectoryAsync(string repoPath, CancellationToken ct) => Task.CompletedTask;
    public Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
    public Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, bool nonNormalize, CancellationToken ct) => throw new NotImplementedException();
    public Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, bool nonNormalize, string? commitSha, CancellationToken ct) => throw new NotImplementedException();
    public Task<(bool Success, string? ErrorMessage)> FetchAsync(string repoPath, bool includeTags, string? bearerToken, CancellationToken ct, bool skipHooks = false) => throw new NotImplementedException();
    public Task<(bool Success, string? ErrorMessage)> FetchMinimalAsync(string repoPath, string branchName, string? defaultBranchOriginRef, string? bearerToken, CancellationToken ct, bool skipUpstreamCheck = false) => throw new NotImplementedException();
    public Task<(bool Success, bool MergeConflict, string? ErrorMessage)> PullAsync(string repoPath, string branchName, string? bearerToken, CancellationToken ct, bool skipHooks = false) => throw new NotImplementedException();
    public Task<(bool Success, string? ErrorMessage)> PushAsync(string repoPath, string branchName, string? bearerToken, bool setTracking = false, CancellationToken ct = default) => throw new NotImplementedException();
    public Task AbortMergeAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
    public Task<(bool Success, bool HasConflicts, IReadOnlyList<string> ConflictFiles, string? ErrorMessage)> MergeFromRemoteAsync(string repoPath, string remoteBranch, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<string>> GetRemoteBranchesAsync(string repoPath, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
    public Task<(bool Success, string? ErrorMessage)> CheckoutBranchAsync(string repoPath, string branchName, CancellationToken ct, bool skipHooks = false) => throw new NotImplementedException();
    public Task<(bool Success, string? ErrorMessage)> CreateBranchAsync(string repoPath, string newBranchName, string baseBranchName, CancellationToken ct, bool skipHooks = false) => throw new NotImplementedException();
    public Task<(bool Success, string? ErrorMessage)> DeleteBranchAsync(string repoPath, string branchName, bool isRemote, bool force, CancellationToken ct, bool skipHooks = false, string? bearerToken = null, string? expectedSha = null) => throw new NotImplementedException();
    public Task<(bool Success, string? ErrorMessage)> FetchTagsAsync(string repoPath, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
    public Task<(bool Success, string? ErrorMessage)> CheckoutTagAsync(string repoPath, string tagName, CancellationToken ct) => throw new NotImplementedException();
    public Task<(bool Success, string? ErrorMessage)> CheckoutCommitAsync(string repoPath, string commit, CancellationToken ct) => throw new NotImplementedException();
    public Task SetDivergenceBaseBranchAsync(string repoPath, string? divergenceBaseBranch, CancellationToken ct) => throw new NotImplementedException();
    public Task<(bool Success, bool Committed, string? ErrorMessage)> StageAndCommitAsync(string repoPath, IReadOnlyList<string> pathsToStage, string commitMessage, CancellationToken ct, bool skipHooks = false) => throw new NotImplementedException();
    public Task<(bool Success, string? ErrorMessage)> ResetToRemoteAsync(string repoPath, string branchName, bool keepChanges, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
    public Task<(bool Success, string? Error)> InitAsync(string repoPath, CancellationToken ct) => Task.FromResult((false, (string?)"Git init failed."));
    public Task<(bool Success, string? Error)> AddRemoteAsync(string repoPath, string name, string url, CancellationToken ct) => throw new NotImplementedException();
    public Task<string?> GetRemoteDefaultBranchAsync(string repoPath, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> RepairOriginHeadAsync(string repoPath, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
    public Task<(bool Success, string? Error)> CheckoutTrackingAsync(string repoPath, string branch, CancellationToken ct) => throw new NotImplementedException();
    public Task<(bool Success, string? Error)> SetUnbornHeadAsync(string repoPath, string branch, CancellationToken ct) => throw new NotImplementedException();
        public Task WriteSyncHooksAsync(string repoPath, int workspaceId, int repositoryId, CancellationToken ct) => throw new NotImplementedException();
    }
}
