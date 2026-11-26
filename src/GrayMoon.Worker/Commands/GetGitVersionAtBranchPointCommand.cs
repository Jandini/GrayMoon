using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// The parent branch's version at a branch point: whether the commit is on <c>origin/&lt;parent&gt;</c>
/// (<c>git merge-base --is-ancestor</c>) and what GitVersion reports for the parent at that commit
/// (<c>/b &lt;parent&gt; /c &lt;sha&gt; /nocache</c>). A version counts only when GitVersion's output names the parent
/// branch and the commit. Never fetches, never changes the checkout.
/// </summary>
public sealed class GetGitVersionAtBranchPointCommand(
    IRepositoryVersionProvider versionProvider,
    GitProcessRunner runner,
    ILogger<GetGitVersionAtBranchPointCommand> logger)
    : ICommandHandler<GetGitVersionAtBranchPointRequest, GetGitVersionAtBranchPointResponse>
{
    public async Task<GetGitVersionAtBranchPointResponse> ExecuteAsync(
        GetGitVersionAtBranchPointRequest request,
        CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        if (string.IsNullOrWhiteSpace(request.WorkspaceRoot))
            throw new ArgumentException("workspaceRoot required");

        var parentBranch = request.ParentBranch?.Trim();
        if (!GitVersionArguments.IsBranchName(parentBranch))
            return Fail($"'{parentBranch}' is not a branch name.");
        var commitSha = request.CommitSha?.Trim();
        if (!GitVersionArguments.IsCommitSha(commitSha))
            return Fail($"'{commitSha}' is not a commit SHA.");

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);
        if (!Directory.Exists(repoPath))
            return Fail("Repository not found.");

        // Exit 0: an ancestor; exit 1: not an ancestor; anything else (no origin/<parent>, bad object) is not an answer.
        // "^{commit}" makes git itself refuse a SHA that is not a commit of this repository, which used to be a
        // git cat-file of its own before this call: one process less for every repository of a new Feature.
        var (ancestorExit, ancestorOut, ancestorErr) = await runner.RunAsync(
            "git", ["merge-base", "--is-ancestor", commitSha + "^{commit}", "refs/remotes/origin/" + parentBranch], repoPath, null, cancellationToken, GitLockIntent.Read);
        if (ancestorExit is not (0 or 1))
        {
            logger.LogInformation(
                "Branch point check failed for {RepoPath}: origin/{Parent} could not be compared with {Commit}. ExitCode={ExitCode}",
                repoPath, parentBranch, commitSha, ancestorExit);
            return Fail(GitCliOutput.CombineOutput(ancestorOut, ancestorErr) ?? $"origin/{parentBranch} could not be compared with {commitSha}.");
        }

        var onRemoteParent = ancestorExit == 0;
        if (!onRemoteParent)
        {
            return new GetGitVersionAtBranchPointResponse
            {
                Success = true,
                BaselineOnRemoteParent = false,
            };
        }

        var versionResult = await versionProvider.GetVersionAsync(
            repoPath,
            new RepositoryVersionOptions { NonNormalize = true, CommitSha = commitSha, BranchName = parentBranch },
            cancellationToken);
        if (!versionResult.Probed)
            return Fail("The Workspace does not calculate versions.", onRemoteParent);
        if (versionResult.Result is null)
            return Fail(versionResult.Error ?? "GitVersion returned no version.", onRemoteParent);

        var mismatch = GitVersionBranchPoint.DescribeMismatch(versionResult.Result, parentBranch!, commitSha!);
        if (mismatch != null)
        {
            logger.LogInformation("Parent version not used for {RepoPath} ({Parent} at {Commit}): {Reason}", repoPath, parentBranch, commitSha, mismatch);
            return Fail(mismatch, onRemoteParent);
        }

        return new GetGitVersionAtBranchPointResponse
        {
            Success = true,
            Version = versionResult.Result.InformationalVersion,
            BaselineOnRemoteParent = true,
        };
    }

    private static GetGitVersionAtBranchPointResponse Fail(string message, bool? onRemoteParent = null) => new()
    {
        Success = false,
        ErrorMessage = message,
        BaselineOnRemoteParent = onRemoteParent,
    };
}
