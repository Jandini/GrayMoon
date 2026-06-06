using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class CreateBranchCommand(IGitService git, IAgentTokenProvider tokenProvider) : ICommandHandler<CreateBranchRequest, CreateBranchResponse>
{
    public async Task<CreateBranchResponse> ExecuteAsync(CreateBranchRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var newBranchName = request.NewBranchName ?? throw new ArgumentException("newBranchName required");
        var baseBranchName = request.BaseBranchName ?? throw new ArgumentException("baseBranchName required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
        {
            return new CreateBranchResponse
            {
                Success = false,
                ErrorMessage = "Repository not found"
            };
        }

        var (success, errorMessage) = await git.CreateBranchAsync(repoPath, newBranchName, baseBranchName, cancellationToken, skipHooks: request.SkipHooks);
        if (!success)
        {
            return new CreateBranchResponse
            {
                Success = false,
                ErrorMessage = errorMessage ?? "Failed to create branch"
            };
        }

        var currentBranch = await git.GetCurrentBranchNameAsync(repoPath, cancellationToken);

        if (!request.SkipHooks)
        {
            return new CreateBranchResponse
            {
                Success = true,
                CurrentBranch = currentBranch
            };
        }

        // Inline sync: collect the same state as CheckoutHookSyncCommand so the app
        // can persist it immediately without waiting for the suppressed post-checkout hook.
        var defaultRef = await git.GetDefaultBranchOriginRefAsync(repoPath, cancellationToken);

        string? token = await tokenProvider.GetTokenForRepositoryAsync(request.RepositoryId, cancellationToken);
        string? fetchError = null;
        if (token != null)
        {
            var (fetchSuccess, err) = await git.FetchMinimalAsync(repoPath, "-", defaultRef, token, cancellationToken);
            if (!fetchSuccess)
                fetchError = err;
        }

        var (versionResult, _) = await git.GetVersionAsync(repoPath, nonNormalize: true, cancellationToken);
        var version = versionResult?.InformationalVersion ?? "-";
        var branch = versionResult?.BranchName ?? versionResult?.EscapedBranchName ?? "-";

        var currentTag = await git.GetCheckedOutTagAsync(repoPath, cancellationToken);
        if (currentTag != null)
            branch = "-";

        int? outgoing = null;
        int? incoming = null;
        int? defaultBehind = null;
        int? defaultAhead = null;
        bool? hasUpstream = null;
        if (branch != "-")
        {
            var (o, i, _) = await git.GetCommitCountsAsync(repoPath, branch, defaultRef, cancellationToken);
            outgoing = o;
            incoming = i;
            var (db, da, _) = await git.GetCommitCountsVsDefaultAsync(repoPath, defaultRef, cancellationToken);
            defaultBehind = db;
            defaultAhead = da;

            var remoteBranches = await git.GetRemoteBranchesFromRefsAsync(repoPath, cancellationToken);
            hasUpstream = remoteBranches.Any(r => string.Equals(r, branch, StringComparison.OrdinalIgnoreCase));
        }

        return new CreateBranchResponse
        {
            Success = true,
            CurrentBranch = currentBranch,
            Version = version,
            Branch = branch,
            Tag = currentTag,
            OutgoingCommits = outgoing,
            IncomingCommits = incoming,
            HasUpstream = hasUpstream,
            DefaultBranchBehind = defaultBehind,
            DefaultBranchAhead = defaultAhead,
            FetchError = fetchError
        };
    }
}
