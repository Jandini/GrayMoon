using GrayMoon.Worker.Abstractions;

namespace GrayMoon.Worker.Services;

public sealed record GitRemoteIntegrateResult(
    bool Success,
    string? Branch,
    string? Version,
    int? Outgoing,
    int? Incoming,
    bool HasUpstream,
    bool MergeConflict,
    string? ErrorMessage)
{
    /// <summary>Pull-only: git refused the fast-forward because local changes would be overwritten.</summary>
    public bool LocalChangesBlockPull { get; init; }

    /// <summary>Pull-only: the branch has commits that are not on its remote branch, so nothing was pulled.</summary>
    public bool HasLocalCommits { get; init; }
}

/// <summary>
/// Fetches from origin and pulls incoming commits before push or commit-sync workflows.
/// Single source of truth for remote integration (fetch + conditional pull).
/// Resolves branch via git only; Version is always null - callers that need SemVer run GitVersion themselves.
/// </summary>
public sealed class GitRemoteIntegrateService(IGitService git, IGitRepositoryReader reader)
{
    public const string LocalChangesBlockPullMessage =
        "Local changes block the pull. Nothing was changed. Discard the changes in this repository, then pull again.";

    public static string LocalCommitsMessage(int outgoing) =>
        outgoing == 1
            ? "The repository is read only and its branch has 1 local commit that is not on the remote. Nothing was pulled."
            : $"The repository is read only and its branch has {outgoing} local commits that are not on the remote. Nothing was pulled.";

    /// <summary>
    /// Fetch, then take incoming commits by fast-forward only. For a read-only repository: nothing is merged, and a
    /// branch with commits of its own is refused before the pull so a diverged history is never touched.
    /// </summary>
    public async Task<GitRemoteIntegrateResult> IntegratePullOnlyAsync(
        string repoPath,
        string? bearerToken,
        CancellationToken cancellationToken = default)
    {
        var (fetchSuccess, fetchError) = await git.FetchAsync(repoPath, includeTags: true, bearerToken, cancellationToken);
        if (!fetchSuccess)
            return new GitRemoteIntegrateResult(false, null, null, null, null, false, false, fetchError ?? "Fetch failed");

        var branch = await reader.GetCurrentBranchNameAsync(repoPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(branch))
            return new GitRemoteIntegrateResult(false, null, null, null, null, false, false, "Could not determine branch name");

        var (outgoing, incoming, hasUpstream) = await reader.GetCommitCountsAsync(repoPath, branch, null, cancellationToken);

        if (outgoing is > 0)
        {
            return new GitRemoteIntegrateResult(false, branch, null, outgoing, incoming, hasUpstream, false, LocalCommitsMessage(outgoing.Value))
            {
                HasLocalCommits = true,
            };
        }

        if (!incoming.HasValue || incoming.Value <= 0)
            return new GitRemoteIntegrateResult(true, branch, null, outgoing, incoming, hasUpstream, false, null);

        var (pullSuccess, localChangesBlock, pullError) = await git.PullFastForwardOnlyAsync(repoPath, branch, bearerToken, cancellationToken, skipHooks: true);
        if (!pullSuccess)
        {
            return new GitRemoteIntegrateResult(
                false, branch, null, outgoing, incoming, hasUpstream, false,
                localChangesBlock ? LocalChangesBlockPullMessage : pullError ?? "Pull failed")
            {
                LocalChangesBlockPull = localChangesBlock,
            };
        }

        (outgoing, incoming, hasUpstream) = await reader.GetCommitCountsAsync(repoPath, branch, null, cancellationToken);
        return new GitRemoteIntegrateResult(true, branch, null, outgoing, incoming, hasUpstream, false, null);
    }

    public async Task<GitRemoteIntegrateResult> IntegrateAsync(
        string repoPath,
        string? bearerToken,
        CancellationToken cancellationToken = default)
    {
        var (fetchSuccess, fetchError) = await git.FetchAsync(repoPath, includeTags: true, bearerToken, cancellationToken);
        if (!fetchSuccess)
        {
            return new GitRemoteIntegrateResult(
                Success: false,
                Branch: null,
                Version: null,
                Outgoing: null,
                Incoming: null,
                HasUpstream: false,
                MergeConflict: false,
                ErrorMessage: fetchError ?? "Fetch failed");
        }

        var branch = await reader.GetCurrentBranchNameAsync(repoPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(branch))
        {
            return new GitRemoteIntegrateResult(
                Success: false,
                Branch: null,
                Version: null,
                Outgoing: null,
                Incoming: null,
                HasUpstream: false,
                MergeConflict: false,
                ErrorMessage: "Could not determine branch name");
        }

        var (outgoing, incoming, hasUpstream) = await reader.GetCommitCountsAsync(repoPath, branch, null, cancellationToken);

        if (!incoming.HasValue || incoming.Value <= 0)
        {
            return new GitRemoteIntegrateResult(
                Success: true,
                Branch: branch,
                Version: null,
                Outgoing: outgoing,
                Incoming: incoming,
                HasUpstream: hasUpstream,
                MergeConflict: false,
                ErrorMessage: null);
        }

        // Skip hooks: this service recomputes commit counts itself right after the pull (below), so a
        // hook-triggered NotifyJob racing this authoritative recompute would just clobber it later.
        var (pullSuccess, mergeConflict, pullError) = await git.PullAsync(repoPath, branch, bearerToken, cancellationToken, skipHooks: true);

        if (mergeConflict)
        {
            await git.AbortMergeAsync(repoPath, cancellationToken);
            await git.FetchAsync(repoPath, includeTags: true, bearerToken, cancellationToken);
            (outgoing, incoming, hasUpstream) = await reader.GetCommitCountsAsync(repoPath, branch, null, cancellationToken);

            return new GitRemoteIntegrateResult(
                Success: false,
                Branch: branch,
                Version: null,
                Outgoing: outgoing,
                Incoming: incoming,
                HasUpstream: hasUpstream,
                MergeConflict: true,
                ErrorMessage: pullError ?? "Merge conflict detected. Merge aborted.");
        }

        if (!pullSuccess)
        {
            return new GitRemoteIntegrateResult(
                Success: false,
                Branch: branch,
                Version: null,
                Outgoing: outgoing,
                Incoming: incoming,
                HasUpstream: hasUpstream,
                MergeConflict: false,
                ErrorMessage: pullError ?? "Pull failed");
        }

        (outgoing, incoming, hasUpstream) = await reader.GetCommitCountsAsync(repoPath, branch, null, cancellationToken);

        return new GitRemoteIntegrateResult(
            Success: true,
            Branch: branch,
            Version: null,
            Outgoing: outgoing,
            Incoming: incoming,
            HasUpstream: hasUpstream,
            MergeConflict: false,
            ErrorMessage: null);
    }
}
