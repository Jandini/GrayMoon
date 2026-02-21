using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Abstractions;

public interface IGitService
{
    string WorkspaceRoot { get; }
    string GetWorkspacePath(string workspaceName);
    Task<bool> CloneAsync(string workingDir, string cloneUrl, string? bearerToken, CancellationToken ct);
    Task AddSafeDirectoryAsync(string repoPath, CancellationToken ct);
    Task<GitVersionResult?> GetVersionAsync(string repoPath, CancellationToken ct);
    Task<string?> GetRemoteOriginUrlAsync(string repoPath, CancellationToken ct);
    /// <summary>Fetches from origin; when <paramref name="includeTags"/> is true, fetches tags as well.</summary>
    Task FetchAsync(string repoPath, bool includeTags, string? bearerToken, CancellationToken ct);
    /// <summary>Returns (outgoing count, incoming count) for the current branch vs origin/branchName. Returns (null, null) if unavailable.</summary>
    Task<(int? Outgoing, int? Incoming)> GetCommitCountsAsync(string repoPath, string branchName, CancellationToken ct);
    /// <summary>Pulls from origin. Returns (success, mergeConflict, errorMessage).</summary>
    Task<(bool Success, bool MergeConflict, string? ErrorMessage)> PullAsync(string repoPath, string branchName, string? bearerToken, CancellationToken ct);
    /// <summary>Pushes to origin. When setTracking is true, uses -u so the branch is upstreamed even when there are no commits to push. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> PushAsync(string repoPath, string branchName, string? bearerToken, bool setTracking = false, CancellationToken ct = default);
    /// <summary>Aborts a merge in progress.</summary>
    Task AbortMergeAsync(string repoPath, CancellationToken ct);
    /// <summary>Gets all local branch names (without 'origin/' prefix).</summary>
    Task<IReadOnlyList<string>> GetLocalBranchesAsync(string repoPath, CancellationToken ct);
    /// <summary>Gets all remote branch names (without 'origin/' prefix).</summary>
    Task<IReadOnlyList<string>> GetRemoteBranchesAsync(string repoPath, CancellationToken ct);
    /// <summary>Checks out the specified branch. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> CheckoutBranchAsync(string repoPath, string branchName, CancellationToken ct);
    /// <summary>Creates a new branch from the given base branch and checks it out. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> CreateBranchAsync(string repoPath, string newBranchName, string baseBranchName, CancellationToken ct);
    /// <summary>Deletes a local branch. Returns true if successful. Only deletes if branch is not current and is merged or force flag is set.</summary>
    Task<bool> DeleteLocalBranchAsync(string repoPath, string branchName, bool force, CancellationToken ct);
    /// <summary>Gets the default branch name (e.g., "main" or "master") without "origin/" prefix.</summary>
    Task<string?> GetDefaultBranchNameAsync(string repoPath, CancellationToken ct);
    /// <summary>Stages the given paths (relative to repo root) and creates a commit with the given message. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> StageAndCommitAsync(string repoPath, IReadOnlyList<string> pathsToStage, string commitMessage, CancellationToken ct);
    void CreateDirectory(string path);
    bool DirectoryExists(string path);
    string[] GetDirectories(string path);
    void WriteSyncHooks(string repoPath, int workspaceId, int repositoryId);
}
