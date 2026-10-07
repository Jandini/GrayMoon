using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

public sealed class CommitSyncRepositoryCommand(
    IGitService git,
    GitRemoteIntegrateService remoteIntegrate,
    IRepositoryStateProbe stateProbe,
    IRepositoryVersionProviderFactory versionProviderFactory) : ICommandHandler<CommitSyncRepositoryRequest, CommitSyncRepositoryResponse>
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

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        if (!Directory.Exists(repoPath))
        {
            return new CommitSyncRepositoryResponse
            {
                Success = false,
                ErrorMessage = "Repository not found"
            };
        }

        await git.SetDivergenceBaseBranchAsync(repoPath, request.DivergenceBaseBranch, cancellationToken);

        var integrate = await remoteIntegrate.IntegrateAsync(repoPath, bearerToken, cancellationToken);
        if (!integrate.Success)
        {
            if (string.IsNullOrWhiteSpace(integrate.Branch))
            {
                return new CommitSyncRepositoryResponse
                {
                    Success = false,
                    MergeConflict = integrate.MergeConflict,
                    Version = await ResolveVersionAsync(repoPath, request, cancellationToken),
                    ErrorMessage = integrate.ErrorMessage
                };
            }

            return await BuildStateResponseAsync(repoPath, request, integrate.Branch, success: false, integrate.MergeConflict, integrate.ErrorMessage, cancellationToken);
        }

        var branch = integrate.Branch!;
        var outgoing = integrate.Outgoing;

        if (!outgoing.HasValue || outgoing.Value <= 0)
            return await BuildStateResponseAsync(repoPath, request, branch, success: true, mergeConflict: false, errorMessage: null, cancellationToken);

        var (pushSuccess, pushError) = await git.PushAsync(repoPath, branch, bearerToken, setTracking: false, ct: cancellationToken);
        if (!pushSuccess)
            return await BuildStateResponseAsync(repoPath, request, branch, success: false, mergeConflict: false, pushError ?? "Push failed", cancellationToken);

        return await BuildStateResponseAsync(repoPath, request, branch, success: true, mergeConflict: false, errorMessage: null, cancellationToken);
    }

    /// <summary>
    /// Reports the state the repository is left in. A pull moves the branch, so it changes the comparison
    /// against the default branch just as much as the one against the upstream; reporting only outgoing and
    /// incoming left the ahead/behind badge showing pre-pull numbers until something else refreshed it.
    /// </summary>
    private async Task<CommitSyncRepositoryResponse> BuildStateResponseAsync(
        string repoPath,
        CommitSyncRepositoryRequest request,
        string branch,
        bool success,
        bool mergeConflict,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var (state, _) = await stateProbe.CaptureAsync(repoPath, new RepositoryStateProbeOptions
        {
            IncludeGitVersion = true,
            BranchNameOverride = branch,
            ErrorMessage = errorMessage,
            Capabilities = request.Capabilities
        }, cancellationToken);

        return new CommitSyncRepositoryResponse
        {
            Success = success,
            MergeConflict = mergeConflict,
            Version = state.GitVersion ?? "-",
            Branch = state.BranchName ?? branch,
            OutgoingCommits = state.OutgoingCommits,
            IncomingCommits = state.IncomingCommits,
            DefaultBranchBehind = state.DefaultBranchBehind,
            DefaultBranchAhead = state.DefaultBranchAhead,
            HasUpstream = state.HasUpstream,
            ErrorMessage = errorMessage,
            State = state
        };
    }

    private async Task<string> ResolveVersionAsync(string repoPath, CommitSyncRepositoryRequest request, CancellationToken cancellationToken)
    {
        var versionResult = await versionProviderFactory
            .Create(request.EffectiveCapabilities)
            .GetVersionAsync(repoPath, RepositoryVersionOptions.Default, cancellationToken);
        return versionResult.VersionOrPlaceholder;
    }
}

