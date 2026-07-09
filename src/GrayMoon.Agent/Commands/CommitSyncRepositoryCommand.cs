using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Commands;

public sealed class CommitSyncRepositoryCommand(IGitService git, GitRemoteIntegrateService remoteIntegrate) : ICommandHandler<CommitSyncRepositoryRequest, CommitSyncRepositoryResponse>
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

        var integrate = await remoteIntegrate.IntegrateAsync(repoPath, bearerToken, cancellationToken);
        if (!integrate.Success)
        {
            var failVersion = await ResolveVersionAsync(repoPath, cancellationToken);
            return new CommitSyncRepositoryResponse
            {
                Success = false,
                MergeConflict = integrate.MergeConflict,
                Version = failVersion,
                Branch = integrate.Branch,
                OutgoingCommits = integrate.Outgoing,
                IncomingCommits = integrate.Incoming,
                ErrorMessage = integrate.ErrorMessage
            };
        }

        var branch = integrate.Branch!;
        var version = await ResolveVersionAsync(repoPath, cancellationToken);
        var outgoing = integrate.Outgoing;
        var incoming = integrate.Incoming;

        if (!outgoing.HasValue || outgoing.Value <= 0)
        {
            return new CommitSyncRepositoryResponse
            {
                Success = true,
                Version = version,
                Branch = branch,
                OutgoingCommits = outgoing,
                IncomingCommits = incoming
            };
        }

        var (pushSuccess, pushError) = await git.PushAsync(repoPath, branch, bearerToken, setTracking: false, ct: cancellationToken);
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

        (outgoing, incoming, _) = await git.GetCommitCountsAsync(repoPath, branch, null, cancellationToken);

        return new CommitSyncRepositoryResponse
        {
            Success = true,
            Version = version,
            Branch = branch,
            OutgoingCommits = outgoing,
            IncomingCommits = incoming
        };
    }

    private async Task<string> ResolveVersionAsync(string repoPath, CancellationToken cancellationToken)
    {
        var (versionResult, _) = await git.GetVersionAsync(repoPath, cancellationToken);
        return versionResult?.InformationalVersion ?? "-";
    }
}
