using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

public sealed class UpdateBranchFromDefaultCommand(IGitService git, ILogger<UpdateBranchFromDefaultCommand> logger) : ICommandHandler<UpdateBranchFromDefaultRequest, UpdateBranchFromDefaultResponse>
{
    public async Task<UpdateBranchFromDefaultResponse> ExecuteAsync(UpdateBranchFromDefaultRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var currentBranchName = request.CurrentBranchName ?? throw new ArgumentException("currentBranchName required");
        var defaultBranchName = request.DefaultBranchName ?? throw new ArgumentException("defaultBranchName required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new UpdateBranchFromDefaultResponse
            {
                Success = false,
                ErrorMessage = "Repository not found"
            };
        }

        // Guard: refuse to start if a merge is already in progress (MERGE_HEAD exists).
        var mergeHeadPath = Path.Combine(repoPath, ".git", "MERGE_HEAD");
        if (File.Exists(mergeHeadPath))
        {
            logger.LogWarning("UpdateBranchFromDefault refused for {RepoPath}: MERGE_HEAD already present", repoPath);
            return new UpdateBranchFromDefaultResponse
            {
                Success = false,
                ErrorMessage = "A merge is already in progress. Resolve the conflicts in your IDE first, then commit."
            };
        }

        // Step 1: Fetch only the default branch from origin (no tags needed, targeted for speed).
        var remoteBranchRef = $"refs/heads/{defaultBranchName}:refs/remotes/origin/{defaultBranchName}";
        var (fetchSuccess, fetchError) = await git.FetchAsync(repoPath, includeTags: false, request.BearerToken, cancellationToken);
        if (!fetchSuccess)
        {
            return new UpdateBranchFromDefaultResponse
            {
                Success = false,
                ErrorMessage = fetchError ?? "Failed to fetch from origin"
            };
        }

        // Step 2: Merge origin/<defaultBranch> into the current branch.
        var remoteBranch = $"origin/{defaultBranchName}";
        var (mergeSuccess, hasConflicts, conflictFiles, mergeError) = await git.MergeFromRemoteAsync(repoPath, remoteBranch, cancellationToken);

        if (hasConflicts)
        {
            logger.LogWarning("Merge conflicts in {RepoPath}: {Files}", repoPath, string.Join(", ", conflictFiles));
            return new UpdateBranchFromDefaultResponse
            {
                Success = false,
                HasConflicts = true,
                ConflictFiles = conflictFiles
            };
        }

        if (!mergeSuccess)
        {
            return new UpdateBranchFromDefaultResponse
            {
                Success = false,
                ErrorMessage = mergeError ?? "Merge failed"
            };
        }

        // Refresh commit counts so the UI can update divergence badges immediately.
        // post-commit hook fires automatically for the merge commit and will send a full sync,
        // but we return fresh counts here so the App can persist them without waiting.
        var defaultRef = await git.GetDefaultBranchOriginRefAsync(repoPath, cancellationToken);
        var (outgoing, incoming, _) = await git.GetCommitCountsAsync(repoPath, currentBranchName, defaultRef, cancellationToken);
        var (defaultBehind, defaultAhead, _) = await git.GetCommitCountsVsDefaultAsync(repoPath, defaultRef, cancellationToken);

        return new UpdateBranchFromDefaultResponse
        {
            Success = true,
            HasConflicts = false,
            OutgoingCommits = outgoing,
            IncomingCommits = incoming,
            DefaultBranchBehind = defaultBehind,
            DefaultBranchAhead = defaultAhead
        };
    }
}
