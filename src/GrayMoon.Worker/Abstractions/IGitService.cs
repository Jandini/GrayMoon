using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using GrayMoon.Common.Git;

namespace GrayMoon.Worker.Abstractions;

public interface IGitService
{
    /// <summary>Clones <paramref name="cloneUrl"/> into a new folder under <paramref name="workingDir"/>. Failure carries a sanitized message; a GitHub SSO URL, when valid, is separate from that message.</summary>
    Task<GitCloneResult> CloneAsync(string workingDir, string cloneUrl, string? bearerToken, CancellationToken ct);
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
    /// <summary>
    /// Same as the commit overload, plus <paramref name="branchName"/> (<c>/b</c>) to version the commit as that branch would
    /// see it. With a branch, <c>/nocache</c> is passed too: GitVersion's own disk cache ignores <c>/b</c> and <c>/c</c> and
    /// would otherwise answer with the result of an earlier run for HEAD. The caller validates the branch name and the SHA.
    /// </summary>
    Task<(GitVersionResult? Result, string? Error)> GetVersionAsync(string repoPath, bool nonNormalize, string? commitSha, string? branchName, CancellationToken ct);
    /// <summary>
    /// Fetches from origin; when <paramref name="includeTags"/> is true, fetches tags as well. Returns (success, errorMessage).
    /// <paramref name="skipHooks"/> runs git without the repository hooks: pass it when the caller reads and reports the branches
    /// itself afterwards, so the reference-transaction hook does not report the same refs a second time.
    /// </summary>
    Task<(bool Success, string? ErrorMessage)> FetchAsync(string repoPath, bool includeTags, string? bearerToken, CancellationToken ct, bool skipHooks = false);
    /// <summary>
    /// Fetches only the refs needed for commit counts: the current branch and the default origin branch (when available),
    /// instead of fetching all remote branches and tags. Returns (success, errorMessage).
    /// </summary>
    Task<(bool Success, string? ErrorMessage)> FetchMinimalAsync(string repoPath, string branchName, string? defaultBranchOriginRef, string? bearerToken, CancellationToken ct, bool skipUpstreamCheck = false);
    /// <summary>Pulls from origin. Returns (success, mergeConflict, errorMessage). When <paramref name="skipHooks"/> is true, hooks are disabled for the pull (orchestrated flows that already recompute and persist commit counts themselves).</summary>
    Task<(bool Success, bool MergeConflict, string? ErrorMessage)> PullAsync(string repoPath, string branchName, string? bearerToken, CancellationToken ct, bool skipHooks = false);
    /// <summary>
    /// Pulls from origin only when the branch can be fast-forwarded: never merges and never rebases. Used for read-only
    /// repositories. <c>LocalChangesBlock</c> is true when git refused because local changes would be overwritten; the
    /// working tree is left as it was.
    /// </summary>
    Task<(bool Success, bool LocalChangesBlock, string? ErrorMessage)> PullFastForwardOnlyAsync(string repoPath, string branchName, string? bearerToken, CancellationToken ct, bool skipHooks = false);
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
    /// <summary>Creates a new branch from the given base branch and checks it out. When a local branch of that name already exists it is checked out as it is, the base is not used, and <c>AlreadyExisted</c> is true so the caller can report it. A create that fails for any other reason is a failure. Returns (success, errorMessage, alreadyExisted, resolvedStartRef, baseCommitSha); the last two are set only when the branch was created from a commit (not for an existing branch or a repository without commits).</summary>
    Task<CreateBranchResult> CreateBranchAsync(string repoPath, string newBranchName, string baseBranchName, CancellationToken ct, bool skipHooks = false);
    /// <summary>Deletes a local or remote branch. Returns (success, errorMessage). For remote, runs git push origin --delete. For local, when <paramref name="force"/> is true, uses git branch -D. When <paramref name="skipHooks"/> is true, hooks are disabled for the remote delete, so the pre-push hook does not queue a sync for a branch the caller is about to leave behind. When deleting a remote branch, pass <paramref name="bearerToken"/> so private remotes authenticate the same way as fetch/pull/push.</summary>
    Task<(bool Success, string? ErrorMessage)> DeleteBranchAsync(string repoPath, string branchName, bool isRemote, bool force, CancellationToken ct, bool skipHooks = false, string? bearerToken = null, string? expectedSha = null);

    /// <summary>
    /// <see cref="DeleteBranchAsync"/> with what the delete saw: the local tip and upstream, why a branch was kept
    /// (<see cref="BranchDeleteFailureKinds"/>), and whether a remote branch was already gone. For a local delete,
    /// <paramref name="expectedSha"/> keeps a branch whose tip is another commit (<c>TipChanged</c>).
    /// </summary>
    async Task<BranchDeleteResult> DeleteBranchDetailedAsync(string repoPath, string branchName, bool isRemote, bool force, CancellationToken ct, bool skipHooks = false, string? bearerToken = null, string? expectedSha = null)
    {
        var (success, errorMessage) = await DeleteBranchAsync(repoPath, branchName, isRemote, force, ct, skipHooks, bearerToken, expectedSha);
        return new BranchDeleteResult(success, errorMessage, success ? null : BranchDeleteFailureKinds.Failed);
    }
    /// <summary>Fetches only tags from origin (git fetch origin --tags). Does not touch branches. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> FetchTagsAsync(string repoPath, string? bearerToken, CancellationToken ct);
    /// <summary>Checks out the specified tag (detached HEAD). Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> CheckoutTagAsync(string repoPath, string tagName, CancellationToken ct);
    /// <summary>Checks out the full commit hash (detached HEAD). Returns (success, errorMessage). Does not substitute another revision when the commit is missing.</summary>
    Task<(bool Success, string? ErrorMessage)> CheckoutCommitAsync(string repoPath, string commit, CancellationToken ct);

    /// <summary>
    /// Puts the local branch <paramref name="branchName"/> at <paramref name="commit"/> and checks it out
    /// (<c>git checkout -B</c>), then sets its upstream to <c>origin/&lt;branch&gt;</c> when that ref exists. Used for a
    /// fresh clone of a read-only repository: the branch is left where the Workspace definition recorded it, and
    /// commits the remote has since gained show as incoming. Fails when the commit is not in the repository.
    /// </summary>
    Task<(bool Success, string? ErrorMessage)> CheckoutBranchAtCommitAsync(string repoPath, string branchName, string commit, CancellationToken ct);
    /// <summary>
    /// Persists or clears the worktree-local divergence base branch (Feature PR parent). Stored under the
    /// worktree-specific git dir so linked Feature worktrees do not share Workspace state. Pass null/empty to clear.
    /// </summary>
    Task SetDivergenceBaseBranchAsync(string repoPath, string? divergenceBaseBranch, CancellationToken ct);
    /// <summary>Stages the given paths (relative to repo root) and creates a commit with the given message. Returns (success, committed, errorMessage). When <paramref name="skipHooks"/> is true, hooks are disabled for add and commit (orchestrated flows such as dependency update that persist state themselves).</summary>
    Task<(bool Success, bool Committed, string? ErrorMessage)> StageAndCommitAsync(string repoPath, IReadOnlyList<string> pathsToStage, string commitMessage, CancellationToken ct, bool skipHooks = false);
    /// <summary>Resets the current branch to origin/<paramref name="branchName"/>. When <paramref name="keepChanges"/> is true uses --mixed (changes remain in working tree); otherwise --hard. If the remote branch does not exist (never pushed), resets to the merge base with the ref its outgoing commits are counted against (<see cref="IGitRepositoryReader.GetNoUpstreamCompareRefAsync"/>) and fails without changing anything when there is none. Never contacts the remote, so it takes no connector token. Returns (success, errorMessage).</summary>
    Task<(bool Success, string? ErrorMessage)> ResetToRemoteAsync(string repoPath, string branchName, bool keepChanges, CancellationToken ct);
    /// <summary>Runs <c>git clone &lt;url&gt; .</c> inside <paramref name="targetDir"/> (an empty directory), so the repository root is that directory. Failure carries a sanitized message.</summary>
    Task<GitCloneResult> CloneIntoAsync(string targetDir, string cloneUrl, string? bearerToken, CancellationToken ct);
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
    /// <summary>
    /// Checks out <paramref name="branch"/> tracking <c>origin/&lt;branch&gt;</c> in a repository with no commit checked out,
    /// without overwriting any file already in the folder: the branch and the index are set, then only missing files
    /// are written. <c>KeptFiles</c> are the existing files that differ from the repository (they show as modified).
    /// </summary>
    Task<(bool Success, string? Error, IReadOnlyList<string> KeptFiles)> CheckoutTrackingKeepingFilesAsync(string repoPath, string branch, CancellationToken ct);
    /// <summary>Points HEAD at an unborn branch with <c>git symbolic-ref HEAD refs/heads/&lt;branch&gt;</c>.</summary>
    Task<(bool Success, string? Error)> SetUnbornHeadAsync(string repoPath, string branch, CancellationToken ct);
    /// <summary>
    /// Installs the shared sync hooks (post-commit/post-checkout/post-merge/pre-push/reference-transaction) once
    /// per common Git directory. Safe to call for a linked worktree (Feature) checkout - resolves the
    /// actual common <c>hooks</c> directory rather than assuming <c>.git</c> at <paramref name="repoPath"/>
    /// is a directory.
    /// </summary>
    Task WriteSyncHooksAsync(string repoPath, int workspaceId, int repositoryId, CancellationToken ct);

    /// <summary>
    /// Asks git where the repository keeps the hooks GrayMoon installed, before they are removed. Runs git, so it is
    /// called before an exclusive claim covers the folder. A missing folder and a folder without <c>.git</c> are
    /// settled as successes.
    /// </summary>
    Task<SyncHookRemovalTarget> ResolveSyncHookRemovalAsync(string repoPath, CancellationToken ct);

    /// <summary>
    /// Deletes GrayMoon's sync hooks from the resolved hooks folders and renames the user's own replaced hooks back.
    /// Only files that carry GrayMoon's marker are deleted. File work only, no process.
    /// </summary>
    SyncHookRemovalResult RemoveSyncHooks(SyncHookRemovalTarget target);




}
