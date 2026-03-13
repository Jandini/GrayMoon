using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class CommitSyncRepositoryCommand(IGitService git) : ICommandHandler<CommitSyncRepositoryRequest, CommitSyncRepositoryResponse>
{
    public async Task<CommitSyncRepositoryResponse> ExecuteAsync(CommitSyncRepositoryRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ExecuteCoreAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            return new CommitSyncRepositoryResponse
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<CommitSyncRepositoryResponse> ExecuteCoreAsync(CommitSyncRepositoryRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var bearerToken = request.BearerToken;

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new CommitSyncRepositoryResponse
            {
                Success = false,
                ErrorMessage = "Repository not found"
            };
        }

        // Get current branch
        var (versionResult, versionError) = await git.GetVersionAsync(repoPath, cancellationToken);
        if (versionResult == null)
        {
            return new CommitSyncRepositoryResponse
            {
                Success = false,
                ErrorMessage = versionError ?? "Could not determine repository version"
            };
        }

        var branch = versionResult.BranchName ?? versionResult.EscapedBranchName;
        if (string.IsNullOrWhiteSpace(branch))
        {
            return new CommitSyncRepositoryResponse
            {
                Success = false,
                ErrorMessage = "Could not determine branch name"
            };
        }

        var version = versionResult.InformationalVersion ?? "-";

        // Fetch first to get latest commit counts
        var (fetchSuccess, fetchError) = await git.FetchAsync(repoPath, includeTags: true, bearerToken, cancellationToken);
        if (!fetchSuccess)
        {
            return new CommitSyncRepositoryResponse
            {
                Success = false,
                Version = version,
                Branch = branch,
                ErrorMessage = fetchError ?? "Fetch failed"
            };
        }

        // Get commit counts (single fetch at start; no refetch after pull/push - refs are already updated)
        var (outgoing, incoming, _) = await git.GetCommitCountsAsync(repoPath, branch, null, cancellationToken);

        bool pullSuccess = true;
        bool mergeConflict = false;

        // Pull if there are incoming commits
        if (incoming.HasValue && incoming.Value > 0)
        {
            var (success, conflict, pullError) = await git.PullAsync(repoPath, branch, bearerToken, cancellationToken);
            pullSuccess = success;
            mergeConflict = conflict;

            if (mergeConflict)
            {
                await git.AbortMergeAsync(repoPath, cancellationToken);
                // One refetch after abort to ensure refs are consistent
                var (refetchOk, _) = await git.FetchAsync(repoPath, includeTags: true, bearerToken, cancellationToken);
                if (!refetchOk)
                {
                    // Refetch failed; still return merge conflict with pull error
                }
                (outgoing, incoming, _) = await git.GetCommitCountsAsync(repoPath, branch, null, cancellationToken);

                return new CommitSyncRepositoryResponse
                {
                    Success = false,
                    MergeConflict = true,
                    Version = version,
                    Branch = branch,
                    OutgoingCommits = outgoing,
                    IncomingCommits = incoming,
                    ErrorMessage = pullError ?? "Merge conflict detected. Merge aborted."
                };
            }

            if (!pullSuccess)
            {
                return new CommitSyncRepositoryResponse
                {
                    Success = false,
                    Version = version,
                    Branch = branch,
                    OutgoingCommits = outgoing,
                    IncomingCommits = incoming,
                    ErrorMessage = pullError ?? "Pull failed"
                };
            }

            // Recompute counts from updated refs (pull already updated refs; no refetch)
            (outgoing, incoming, _) = await git.GetCommitCountsAsync(repoPath, branch, null, cancellationToken);
        }

        // Push if there are outgoing commits
        bool pushSuccess = true;
        if (outgoing.HasValue && outgoing.Value > 0)
        {
            var (success, pushError) = await git.PushAsync(repoPath, branch, bearerToken, setTracking: false, ct: cancellationToken);
            pushSuccess = success;

            if (!pushSuccess)
            {
                return new CommitSyncRepositoryResponse
                {
                    Success = false,
                    Version = version,
                    Branch = branch,
                    OutgoingCommits = outgoing,
                    IncomingCommits = incoming,
                    ErrorMessage = pushError ?? "Push failed"
                };
            }

            // Recompute counts from updated refs (push already updated refs; no refetch)
            (outgoing, incoming, _) = await git.GetCommitCountsAsync(repoPath, branch, null, cancellationToken);
        }

        return new CommitSyncRepositoryResponse
        {
            Success = true,
            Version = version,
            Branch = branch,
            OutgoingCommits = outgoing,
            IncomingCommits = incoming
        };
    }
}
