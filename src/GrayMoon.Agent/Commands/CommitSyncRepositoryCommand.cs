using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Models;

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

        var version = versionResult.SemVer ?? versionResult.FullSemVer ?? "-";

        // Fetch first to get latest commit counts
        await git.FetchAsync(repoPath, includeTags: false, bearerToken, cancellationToken);

        // Get commit counts
        var (outgoing, incoming) = await git.GetCommitCountsAsync(repoPath, branch, cancellationToken);

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
                // Abort merge for this repo
                await git.AbortMergeAsync(repoPath, cancellationToken);
                
                // Refresh commit counts after abort
                await git.FetchAsync(repoPath, includeTags: false, bearerToken, cancellationToken);
                (outgoing, incoming) = await git.GetCommitCountsAsync(repoPath, branch, cancellationToken);

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

            // Refresh commit counts after pull
            await git.FetchAsync(repoPath, includeTags: false, bearerToken, cancellationToken);
            (outgoing, incoming) = await git.GetCommitCountsAsync(repoPath, branch, cancellationToken);
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

            // Refresh commit counts after push
            await git.FetchAsync(repoPath, includeTags: false, bearerToken, cancellationToken);
            (outgoing, incoming) = await git.GetCommitCountsAsync(repoPath, branch, cancellationToken);
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
