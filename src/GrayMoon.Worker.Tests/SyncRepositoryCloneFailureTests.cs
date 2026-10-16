using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

public sealed class SyncRepositoryCloneFailureTests : IDisposable
{
    private const string Secret = "SSOQUERYTOKEN";
    private const string SsoUrl = "https://github.com/enterprises/acme-labs-global/sso?authorization_request=" + Secret;

    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-sync-clone-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Failed_sso_clone_is_not_a_successful_sync_and_skips_fetch()
    {
        var git = new CloneGit
        {
            OnClone = _ => GitCloneResult.FromFailure(new GitRemoteFailure(
                128, GitRemoteFailureKind.SsoAuthorizationRequired,
                "GitHub SSO authorization required. Reauthorize Visual Studio to access acme-labs. Complete authorization in your browser, then retry cloning.",
                SsoUrl, "Visual Studio", "acme-labs"))
        };
        var response = await Command(git).ExecuteAsync(Request("https://github.com/acme-labs/cifx.git"));

        Assert.False(response.Success);
        Assert.True(response.NotCloned);
        Assert.Equal(GitRemoteFailureKind.SsoAuthorizationRequired, response.FailureKind);
        Assert.Equal(SsoUrl, response.SsoAuthorizationUrl);
        Assert.Contains("Visual Studio", response.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, response.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, git.FetchCalls);
        Assert.Equal(1, git.CloneCalls);
    }

    [Fact]
    public async Task Successful_clone_continues_to_fetch()
    {
        var git = new CloneGit
        {
            OnClone = workspace =>
            {
                var repo = Path.Combine(workspace, "cifx");
                Directory.CreateDirectory(Path.Combine(repo, ".git"));
                return GitCloneResult.Succeeded();
            }
        };

        var response = await Command(git).ExecuteAsync(Request("https://github.com/acme/cifx.git"));

        Assert.Equal(1, git.CloneCalls);
        Assert.Equal(1, git.FetchCalls);
        Assert.False(response.Success);
        Assert.Equal("Git fetch failed.", response.ErrorMessage);
    }

    [Fact]
    public async Task Exit_zero_without_a_repository_is_not_success()
    {
        var git = new CloneGit { OnClone = _ => GitCloneResult.Succeeded() };

        var response = await Command(git).ExecuteAsync(Request("https://github.com/acme/cifx.git"));

        Assert.False(response.Success);
        Assert.True(response.NotCloned);
        Assert.Equal(0, git.FetchCalls);
        Assert.Contains("did not create a repository", response.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_clone_url_is_not_cloned()
    {
        var git = new CloneGit();

        var response = await Command(git).ExecuteAsync(Request(cloneUrl: null));

        Assert.False(response.Success);
        Assert.True(response.NotCloned);
        Assert.Equal(0, git.CloneCalls);
        Assert.Contains("no clone URL", response.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preexisting_checkout_is_fetched_and_other_files_are_left_in_place()
    {
        var repo = Path.Combine(_root, "ws", "cifx");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var git = new CloneGit();

        var existing = await Command(git).ExecuteAsync(Request("https://github.com/acme/cifx.git"));

        Assert.False(existing.Success);
        Assert.Equal(0, git.CloneCalls);
        Assert.Equal(1, git.FetchCalls);
        Assert.True(Directory.Exists(Path.Combine(repo, ".git")));

        Directory.Delete(repo, recursive: true);
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "notes.txt"), "keep me\n");

        var kept = await Command(git).ExecuteAsync(Request("https://github.com/acme/cifx.git"));

        Assert.False(kept.Success);
        Assert.Equal(0, git.CloneCalls);
        Assert.Equal(1, git.FetchCalls);
        Assert.Equal("keep me\n", File.ReadAllText(Path.Combine(repo, "notes.txt")));
        Assert.Contains("left in place", kept.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clone_that_leaves_only_git_metadata_is_removed_so_retry_can_clone()
    {
        var git = new CloneGit
        {
            OnClone = workspace =>
            {
                Directory.CreateDirectory(Path.Combine(workspace, "cifx", ".git"));
                return GitCloneResult.FromFailure(new GitRemoteFailure(128, GitRemoteFailureKind.AccessDenied, "Git could not access this repository (HTTP 403).", null, null, null));
            }
        };

        var response = await Command(git).ExecuteAsync(Request("https://github.com/acme/cifx.git"));

        Assert.False(response.Success);
        Assert.DoesNotContain("SSO", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, "ws", "cifx")));
    }

    private SyncRepositoryCommand Command(CloneGit git)
        => new(git, null!, null!, null!, null!);

    private SyncRepositoryRequest Request(string? cloneUrl) => new()
    {
        WorkspaceRoot = _root,
        WorkspaceName = "ws",
        RepositoryName = "cifx",
        RepositoryId = 1,
        WorkspaceId = 1,
        CloneUrl = cloneUrl,
    };

    private sealed class CloneGit : IGitService
    {
        public int FetchCalls { get; private set; }
        public int CloneCalls { get; set; }
        public Func<string, GitCloneResult>? OnClone { get; set; }

        public Task<GitCloneResult> CloneAsync(string workingDir, string cloneUrl, string? bearerToken, CancellationToken ct)
        {
            CloneCalls++;
            return Task.FromResult(OnClone?.Invoke(workingDir) ?? GitCloneResult.Succeeded());
        }

        public Task<(bool Success, string? ErrorMessage)> FetchAsync(string repoPath, bool includeTags, string? bearerToken, CancellationToken ct, bool skipHooks = false)
        {
            FetchCalls++;
            return Task.FromResult<(bool, string?)>((false, "Git fetch failed."));
        }

        public Task AddSafeDirectoryAsync(string repoPath, CancellationToken ct) => Task.CompletedTask;
        public Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
        public Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, bool nonNormalize, CancellationToken ct) => throw new NotImplementedException();
        public Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, bool nonNormalize, string? commitSha, CancellationToken ct) => throw new NotImplementedException();
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
        public Task<GitCloneResult> CloneIntoAsync(string targetDir, string cloneUrl, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> InitAsync(string repoPath, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> AddRemoteAsync(string repoPath, string name, string url, CancellationToken ct) => throw new NotImplementedException();
        public Task<string?> GetRemoteDefaultBranchAsync(string repoPath, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> RepairOriginHeadAsync(string repoPath, string? bearerToken, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> CheckoutTrackingAsync(string repoPath, string branch, CancellationToken ct) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> SetUnbornHeadAsync(string repoPath, string branch, CancellationToken ct) => throw new NotImplementedException();
        public Task WriteSyncHooksAsync(string repoPath, int workspaceId, int repositoryId, CancellationToken ct) => throw new NotImplementedException();
    }
}
