using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using GrayMoon.Common.Git;

namespace GrayMoon.Worker.Abstractions;

public interface IGitService
{
    Task<bool> CloneAsync(string workingDir, string cloneUrl, string? bearerToken, CancellationToken ct);
    Task AddSafeDirectoryAsync(string repoPath, CancellationToken ct);
    Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, CancellationToken ct);
    /// <summary>
    /// Runs GitVersion with /output json and /nofetch. When <paramref name="nonNormalize"/> is true,
    /// passes /nonormalize to disable commit graph normalization for faster execution in flows that
    /// have already ensured fetch ordering (e.g. minimal fetch).
    /// </summary>
    Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, bool nonNormalize, CancellationToken ct);
    /// <summary>
    /// Same as the nonNormalize overload, plus optional <paramref name="commitSha"/> (<c>/c</c>) to version a
    /// specific commit (e.g. tip of <c>origin/main</c>) without checking it out.
    /// </summary>
    Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, bool nonNormalize, string? commitSha, CancellationToken ct);
    /// <summary>Fetches from origin; when <paramref name="includeTags"/> is true, fetches tags as well. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> FetchAsync(string repoPath, bool includeTags, string? bearerToken, CancellationToken ct);
    /// <summary>
    /// Fetches only the refs needed for commit counts: the current branch and the default origin branch (when available),
    /// instead of fetching all remote branches and tags. Returns (success, errorMessage).
    /// </summary>
    Task<(bool Success, string? ErrorMessage)> FetchMinimalAsync(string repoPath, string branchName, string? defaultBranchOriginRef, string? bearerToken, CancellationToken ct, bool skipUpstreamCheck = false);
    /// <summary>Pulls from origin. Returns (success, mergeConflict, errorMessage). When <paramref name="skipHooks"/> is true, hooks are disabled for the pull (orchestrated flows that already recompute and persist commit counts themselves).</summary>
    Task<(bool Success, bool MergeConflict, string? ErrorMessage)> PullAsync(string repoPath, string branchName, string? bearerToken, CancellationToken ct, bool skipHooks = false);
    /// <summary>Pushes to origin. When setTracking is true, uses -u so the branch is upstreamed even when there are no commits to push. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> PushAsync(string repoPath, string branchName, string? bearerToken, bool setTracking = false, CancellationToken ct = default);
    /// <summary>Aborts a merge in progress.</summary>
    Task AbortMergeAsync(string repoPath, CancellationToken ct);
    /// <summary>
    /// Merges <paramref name="remoteBranch"/> (e.g. "origin/main") into the current branch using
    /// <c>git merge --no-edit</c>. Returns (Success, HasConflicts, ConflictFiles, ErrorMessage).
    /// When <see cref="HasConflicts"/> is true the repo is left in MERGE_HEAD state for the user to
    /// resolve; the caller must NOT abort - the user resolves in their IDE then commits.
    /// </summary>
    Task<(bool Success, bool HasConflicts, IReadOnlyList<string> ConflictFiles, string? ErrorMessage)> MergeFromRemoteAsync(string repoPath, string remoteBranch, CancellationToken ct);
    /// <summary>Gets all remote branch names (without 'origin/' prefix). Uses ls-remote; for post-fetch use <see cref="GetRemoteBranchesFromRefsAsync"/>.</summary>
    Task<IReadOnlyList<string>> GetRemoteBranchesAsync(string repoPath, string? bearerToken, CancellationToken ct);
    /// <summary>Checks out the specified branch. Returns (success, errorMessage). When <paramref name="skipHooks"/> is true, hooks are disabled for the checkout (orchestrated flows such as return-to-default).</summary>
    Task<(bool Success, string? ErrorMessage)> CheckoutBranchAsync(string repoPath, string branchName, CancellationToken ct, bool skipHooks = false);
    /// <summary>Creates a new branch from the given base branch and checks it out. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> CreateBranchAsync(string repoPath, string newBranchName, string baseBranchName, CancellationToken ct, bool skipHooks = false);
    /// <summary>Deletes a local or remote branch. Returns (success, errorMessage). For remote, runs git push origin --delete. For local, when <paramref name="force"/> is true, uses git branch -D. When <paramref name="skipHooks"/> is true, hooks are disabled for the remote delete, so the pre-push hook does not queue a sync for a branch the caller is about to leave behind. When deleting a remote branch, pass <paramref name="bearerToken"/> so private remotes authenticate the same way as fetch/pull/push.</summary>
    Task<(bool Success, string? ErrorMessage)> DeleteBranchAsync(string repoPath, string branchName, bool isRemote, bool force, CancellationToken ct, bool skipHooks = false, string? bearerToken = null, string? expectedSha = null);
    /// <summary>Fetches only tags from origin (git fetch origin --tags). Does not touch branches. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> FetchTagsAsync(string repoPath, string? bearerToken, CancellationToken ct);
    /// <summary>Checks out the specified tag (detached HEAD). Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> CheckoutTagAsync(string repoPath, string tagName, CancellationToken ct);
    /// <summary>Checks out the full commit hash (detached HEAD). Returns (success, errorMessage). Does not substitute another revision when the commit is missing.</summary>
    Task<(bool Success, string? ErrorMessage)> CheckoutCommitAsync(string repoPath, string commit, CancellationToken ct);
    /// <summary>
    /// Persists or clears the worktree-local divergence base branch (Feature PR parent). Stored under the
    /// worktree-specific git dir so linked Feature worktrees do not share Workspace state. Pass null/empty to clear.
    /// </summary>
    Task SetDivergenceBaseBranchAsync(string repoPath, string? divergenceBaseBranch, CancellationToken ct);
    /// <summary>Stages the given paths (relative to repo root) and creates a commit with the given message. Returns (success, committed, errorMessage). When <paramref name="skipHooks"/> is true, hooks are disabled for add and commit (orchestrated flows such as dependency update that persist state themselves).</summary>
    Task<(bool Success, bool Committed, string? ErrorMessage)> StageAndCommitAsync(string repoPath, IReadOnlyList<string> pathsToStage, string commitMessage, CancellationToken ct, bool skipHooks = false);
    /// <summary>Resets the current branch to origin/<paramref name="branchName"/>. When <paramref name="keepChanges"/> is true uses --mixed (changes remain in working tree); otherwise --hard. If the remote branch does not exist, pushes it upstream first using <paramref name="bearerToken"/>. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> ResetToRemoteAsync(string repoPath, string branchName, bool keepChanges, string? bearerToken, CancellationToken ct);
    /// <summary>Runs <c>git clone &lt;url&gt; .</c> inside <paramref name="targetDir"/> (an empty directory), so the repository root is that directory. Returns success.</summary>
    Task<bool> CloneIntoAsync(string targetDir, string cloneUrl, string? bearerToken, CancellationToken ct);
    /// <summary>Runs <c>git init</c> in <paramref name="repoPath"/>.</summary>
    Task<(bool Success, string? Error)> InitAsync(string repoPath, CancellationToken ct);
    /// <summary>Runs <c>git remote add &lt;name&gt; &lt;url&gt;</c>.</summary>
    Task<(bool Success, string? Error)> AddRemoteAsync(string repoPath, string name, string url, CancellationToken ct);
    /// <summary>Returns the remote HEAD branch name (from <c>git ls-remote --symref origin HEAD</c>), or null when the remote is empty or unreachable.</summary>
    Task<string?> GetRemoteDefaultBranchAsync(string repoPath, string? bearerToken, CancellationToken ct);
    /// <summary>
    /// Points <c>refs/remotes/origin/HEAD</c> at the remote's current default branch (asked with <c>ls-remote --symref</c>, set with <c>git remote set-head origin &lt;branch&gt;</c>). Meant for when it is missing or dangling, which a fetch never repairs. Returns true only when it was repointed. Never throws for a failed lookup or a refused <c>set-head</c>; after an attempt that did not repair it, the same repository is not asked again for a while, so a remote that cannot answer does not cost a round trip on every sync.
    /// </summary>
    Task<bool> RepairOriginHeadAsync(string repoPath, string? bearerToken, CancellationToken ct);
    /// <summary>Runs <c>git checkout -b &lt;branch&gt; --track origin/&lt;branch&gt;</c>; the error is git's own message verbatim.</summary>
    Task<(bool Success, string? Error)> CheckoutTrackingAsync(string repoPath, string branch, CancellationToken ct);
    /// <summary>Points HEAD at an unborn branch with <c>git symbolic-ref HEAD refs/heads/&lt;branch&gt;</c>.</summary>
    Task<(bool Success, string? Error)> SetUnbornHeadAsync(string repoPath, string branch, CancellationToken ct);
    /// <summary>
    /// Installs the shared sync hooks (post-commit/post-checkout/post-merge/post-update/pre-push) once
    /// per common Git directory. Safe to call for a linked worktree (Feature) checkout - resolves the
    /// actual common <c>hooks</c> directory rather than assuming <c>.git</c> at <paramref name="repoPath"/>
    /// is a directory.
    /// </summary>
    Task WriteSyncHooksAsync(string repoPath, int workspaceId, int repositoryId, CancellationToken ct);




}
