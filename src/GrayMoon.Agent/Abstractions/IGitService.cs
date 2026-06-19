using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Abstractions;

public interface IGitService
{
    string GetWorkspacePath(string root, string workspaceName);
    Task<bool> CloneAsync(string workingDir, string cloneUrl, string? bearerToken, CancellationToken ct);
    Task AddSafeDirectoryAsync(string repoPath, CancellationToken ct);
    Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, CancellationToken ct);
    /// <summary>
    /// Runs GitVersion with /output json and /nofetch. When <paramref name="nonNormalize"/> is true,
    /// passes /nonormalize to disable commit graph normalization for faster execution in flows that
    /// have already ensured fetch ordering (e.g. minimal fetch).
    /// </summary>
    Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, bool nonNormalize, CancellationToken ct);
    /// <summary>Gets the current branch name (e.g. "main") with a single git call. Use instead of GetVersionAsync when only branch name is needed.</summary>
    Task<string?> GetCurrentBranchNameAsync(string repoPath, CancellationToken ct);
    Task<string?> GetRemoteOriginUrlAsync(string repoPath, CancellationToken ct);
    /// <summary>Fetches from origin; when <paramref name="includeTags"/> is true, fetches tags as well. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> FetchAsync(string repoPath, bool includeTags, string? bearerToken, CancellationToken ct);
    /// <summary>
    /// Fetches only the refs needed for commit counts: the current branch and the default origin branch (when available),
    /// instead of fetching all remote branches and tags. Returns (success, errorMessage).
    /// </summary>
    Task<(bool Success, string? ErrorMessage)> FetchMinimalAsync(string repoPath, string branchName, string? defaultBranchOriginRef, string? bearerToken, CancellationToken ct, bool skipUpstreamCheck = false);
    /// <summary>Returns (outgoing count, incoming count, hasUpstream) for the current branch vs origin/branchName. When the branch has no upstream, returns (null, null) or (aheadOfDefault, null) and hasUpstream false. When <paramref name="defaultBranchOriginRef"/> is provided and branch has no upstream, uses it instead of resolving default again.</summary>
    Task<(int? Outgoing, int? Incoming, bool HasUpstream)> GetCommitCountsAsync(string repoPath, string branchName, string? defaultBranchOriginRef, CancellationToken ct, bool skipUpstreamCheck = false);
    /// <summary>Returns (behind, ahead, defaultBranchName) for the current branch vs the default branch. DefaultBranchName is without "origin/" prefix. When <paramref name="defaultBranchOriginRef"/> is provided, uses it instead of resolving.</summary>
    Task<(int? DefaultBehind, int? DefaultAhead, string? DefaultBranchName)> GetCommitCountsVsDefaultAsync(string repoPath, string? defaultBranchOriginRef, CancellationToken ct);
    /// <summary>Pulls from origin. Returns (success, mergeConflict, errorMessage).</summary>
    Task<(bool Success, bool MergeConflict, string? ErrorMessage)> PullAsync(string repoPath, string branchName, string? bearerToken, CancellationToken ct);
    /// <summary>Pushes to origin. When setTracking is true, uses -u so the branch is upstreamed even when there are no commits to push. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> PushAsync(string repoPath, string branchName, string? bearerToken, bool setTracking = false, CancellationToken ct = default);
    /// <summary>Aborts a merge in progress.</summary>
    Task AbortMergeAsync(string repoPath, CancellationToken ct);
    /// <summary>Gets all local branch names (without 'origin/' prefix).</summary>
    Task<IReadOnlyList<string>> GetLocalBranchesAsync(string repoPath, CancellationToken ct);
    /// <summary>Gets all remote branch names from local refs (refs/remotes/origin). Use after fetch to avoid ls-remote network call.</summary>
    Task<IReadOnlyList<string>> GetRemoteBranchesFromRefsAsync(string repoPath, CancellationToken ct);
    /// <summary>Gets all remote branch names (without 'origin/' prefix). Uses ls-remote; for post-fetch use <see cref="GetRemoteBranchesFromRefsAsync"/>.</summary>
    Task<IReadOnlyList<string>> GetRemoteBranchesAsync(string repoPath, string? bearerToken, CancellationToken ct);
    /// <summary>Checks out the specified branch. Returns (success, errorMessage). When <paramref name="skipHooks"/> is true, hooks are disabled for the checkout (orchestrated flows such as sync-to-default).</summary>
    Task<(bool Success, string? ErrorMessage)> CheckoutBranchAsync(string repoPath, string branchName, CancellationToken ct, bool skipHooks = false);
    /// <summary>Creates a new branch from the given base branch and checks it out. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> CreateBranchAsync(string repoPath, string newBranchName, string baseBranchName, CancellationToken ct, bool skipHooks = false);
    /// <summary>Deletes a local or remote branch. Returns (success, errorMessage). For remote, runs git push origin --delete. For local, when <paramref name="force"/> is true, uses git branch -D.</summary>
    Task<(bool Success, string? ErrorMessage)> DeleteBranchAsync(string repoPath, string branchName, bool isRemote, bool force, CancellationToken ct);
    /// <summary>Gets the default branch name (e.g., "main" or "master") without "origin/" prefix.</summary>
    Task<string?> GetDefaultBranchNameAsync(string repoPath, CancellationToken ct);
    /// <summary>Gets all tag names in the repository (newest first when supported, then alphabetical).</summary>
    Task<IReadOnlyList<string>> GetTagsAsync(string repoPath, CancellationToken ct);
    /// <summary>Fetches only tags from origin (git fetch origin --tags). Does not touch branches. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> FetchTagsAsync(string repoPath, string? bearerToken, CancellationToken ct);
    /// <summary>Checks out the specified tag (detached HEAD). Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> CheckoutTagAsync(string repoPath, string tagName, CancellationToken ct);
    /// <summary>Returns the tag name HEAD currently points to when the repo is in a detached HEAD state AND that commit is the exact tip of a tag; otherwise null. Uses git symbolic-ref + describe --tags --exact-match.</summary>
    Task<string?> GetCheckedOutTagAsync(string repoPath, CancellationToken ct);
    /// <summary>Gets the default branch origin ref (e.g., "origin/main") for passing to GetCommitCountsAsync/GetCommitCountsVsDefaultAsync to avoid resolving twice.</summary>
    Task<string?> GetDefaultBranchOriginRefAsync(string repoPath, CancellationToken ct);
    /// <summary>Stages the given paths (relative to repo root) and creates a commit with the given message. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> StageAndCommitAsync(string repoPath, IReadOnlyList<string> pathsToStage, string commitMessage, CancellationToken ct);
    /// <summary>Resets the current branch to origin/<paramref name="branchName"/>. When <paramref name="keepChanges"/> is true uses --mixed (changes remain in working tree); otherwise --hard. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> ResetToRemoteAsync(string repoPath, string branchName, bool keepChanges, CancellationToken ct);
    void CreateDirectory(string path);
    bool DirectoryExists(string path);
    string[] GetDirectories(string path);
    void WriteSyncHooks(string repoPath, int workspaceId, int repositoryId);
}
