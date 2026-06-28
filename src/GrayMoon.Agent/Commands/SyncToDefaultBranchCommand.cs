using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

public sealed class SyncToDefaultBranchCommand(IGitService git, ICsProjFileService csProjFileService, ILogger<SyncToDefaultBranchCommand> logger) : ICommandHandler<SyncToDefaultBranchRequest, SyncToDefaultBranchResponse>
{
    public async Task<SyncToDefaultBranchResponse> ExecuteAsync(SyncToDefaultBranchRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var currentBranchName = request.CurrentBranchName ?? throw new ArgumentException("currentBranchName required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new SyncToDefaultBranchResponse
            {
                Success = false,
                ErrorMessage = "Repository not found"
            };
        }

        // Resolve default branch name first (needed for safety check before remote delete)
        var defaultBranch = await git.GetDefaultBranchNameAsync(repoPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(defaultBranch))
        {
            return new SyncToDefaultBranchResponse
            {
                Success = false,
                ErrorMessage = "Could not determine default branch"
            };
        }

        // If the user confirmed remote branch deletion, delete it before fetch so --prune removes the tracking ref
        if (request.DeleteRemoteBranch && !string.Equals(currentBranchName, defaultBranch, StringComparison.OrdinalIgnoreCase))
        {
            var (remoteDeleteOk, remoteDeleteErr) = await git.DeleteBranchAsync(repoPath, currentBranchName, isRemote: true, force: false, cancellationToken);
            if (!remoteDeleteOk)
                logger.LogWarning("Remote branch delete failed for {Branch} in {RepoPath}: {Error}", currentBranchName, repoPath, remoteDeleteErr);
        }

        // Fetch to update remote-tracking refs and prune deleted remote branches
        var (fetchSuccess, fetchError) = await git.FetchAsync(repoPath, includeTags: true, request.BearerToken, cancellationToken);
        if (!fetchSuccess)
        {
            return new SyncToDefaultBranchResponse
            {
                Success = false,
                ErrorMessage = fetchError ?? "Failed to fetch from origin"
            };
        }

        // Checkout default branch without firing post-checkout hook (orchestrated fetch/pull follows).
        var (checkoutSuccess, checkoutError) = await git.CheckoutBranchAsync(repoPath, defaultBranch, cancellationToken, skipHooks: true);
        if (!checkoutSuccess)
        {
            return new SyncToDefaultBranchResponse
            {
                Success = false,
                ErrorMessage = checkoutError ?? "Failed to checkout default branch"
            };
        }

        // Delete the old branch (only if it's not the same as default)
        if (currentBranchName != defaultBranch)
        {
            // Force delete (-D) only when PR is merged (set by App from current PR status). Otherwise use -d so we only delete if merged locally.
            var (localDeleteOk, localDeleteErr) = await git.DeleteBranchAsync(repoPath, currentBranchName, isRemote: false, force: request.ForceDeleteLocalBranch, cancellationToken);
            if (!localDeleteOk)
                logger.LogWarning("Local branch delete failed for {Branch} in {RepoPath}: {Error}", currentBranchName, repoPath, localDeleteErr);
        }

        // Always pull after switching to default so local is up to date
        var (pullSuccess, mergeConflict, pullError) = await git.PullAsync(repoPath, defaultBranch, request.BearerToken, cancellationToken);
        if (!pullSuccess)
        {
            return new SyncToDefaultBranchResponse
            {
                Success = false,
                ErrorMessage = mergeConflict ? (pullError ?? "Merge conflict after pull.") : (pullError ?? "Failed to pull after switching to default branch"),
                CurrentBranch = defaultBranch,
                DefaultBranch = defaultBranch
            };
        }

        var defaultRef = await git.GetDefaultBranchOriginRefAsync(repoPath, cancellationToken);
        var (outgoing, incoming, hasUpstream) = await git.GetCommitCountsAsync(repoPath, defaultBranch, defaultRef, cancellationToken);
        var (defaultBehind, defaultAhead, _) = await git.GetCommitCountsVsDefaultAsync(repoPath, defaultRef, cancellationToken);

        var localBranches = await git.GetLocalBranchesAsync(repoPath, cancellationToken);
        var remoteBranches = await git.GetRemoteBranchesFromRefsAsync(repoPath, cancellationToken);
        var tags = await git.GetTagsAsync(repoPath, cancellationToken);
        var currentTag = await git.GetCheckedOutTagAsync(repoPath, cancellationToken);

        var (versionResult, _) = await git.GetVersionAsync(repoPath, cancellationToken);
        var gitVersion = versionResult?.InformationalVersion;

        var projects = await csProjFileService.FindAsync(repoPath, cancellationToken);

        return new SyncToDefaultBranchResponse
        {
            Success = true,
            CurrentBranch = defaultBranch,
            DefaultBranch = defaultBranch,
            LocalBranches = localBranches,
            RemoteBranches = remoteBranches,
            Tags = tags,
            CurrentTag = currentTag,
            OutgoingCommits = outgoing,
            IncomingCommits = incoming,
            HasUpstream = hasUpstream,
            DefaultBranchBehind = defaultBehind,
            DefaultBranchAhead = defaultAhead,
            GitVersion = gitVersion,
            Projects = projects
        };
    }
}
