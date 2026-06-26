using GrayMoon.Agent.Abstractions;

namespace GrayMoon.Agent.Services;

public sealed record GitRemoteIntegrateResult(
    bool Success,
    string? Branch,
    string? Version,
    int? Outgoing,
    int? Incoming,
    bool HasUpstream,
    bool MergeConflict,
    string? ErrorMessage);

/// <summary>
/// Fetches from origin and pulls incoming commits before push or commit-sync workflows.
/// Single source of truth for remote integration (fetch + conditional pull).
/// </summary>
public sealed class GitRemoteIntegrateService(IGitService git)
{
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

        var (versionResult, versionError) = await git.GetVersionAsync(repoPath, cancellationToken);
        if (versionResult == null)
        {
            return new GitRemoteIntegrateResult(
                Success: false,
                Branch: null,
                Version: null,
                Outgoing: null,
                Incoming: null,
                HasUpstream: false,
                MergeConflict: false,
                ErrorMessage: versionError ?? "Could not determine repository version");
        }

        var branch = versionResult.BranchName ?? versionResult.EscapedBranchName;
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

        var version = versionResult.InformationalVersion ?? "-";
        var (outgoing, incoming, hasUpstream) = await git.GetCommitCountsAsync(repoPath, branch, null, cancellationToken);

        if (!incoming.HasValue || incoming.Value <= 0)
        {
            return new GitRemoteIntegrateResult(
                Success: true,
                Branch: branch,
                Version: version,
                Outgoing: outgoing,
                Incoming: incoming,
                HasUpstream: hasUpstream,
                MergeConflict: false,
                ErrorMessage: null);
        }

        var (pullSuccess, mergeConflict, pullError) = await git.PullAsync(repoPath, branch, bearerToken, cancellationToken);

        if (mergeConflict)
        {
            await git.AbortMergeAsync(repoPath, cancellationToken);
            await git.FetchAsync(repoPath, includeTags: true, bearerToken, cancellationToken);
            (outgoing, incoming, hasUpstream) = await git.GetCommitCountsAsync(repoPath, branch, null, cancellationToken);

            return new GitRemoteIntegrateResult(
                Success: false,
                Branch: branch,
                Version: version,
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
                Version: version,
                Outgoing: outgoing,
                Incoming: incoming,
                HasUpstream: hasUpstream,
                MergeConflict: false,
                ErrorMessage: pullError ?? "Pull failed");
        }

        (outgoing, incoming, hasUpstream) = await git.GetCommitCountsAsync(repoPath, branch, null, cancellationToken);

        return new GitRemoteIntegrateResult(
            Success: true,
            Branch: branch,
            Version: version,
            Outgoing: outgoing,
            Incoming: incoming,
            HasUpstream: hasUpstream,
            MergeConflict: false,
            ErrorMessage: null);
    }
}
