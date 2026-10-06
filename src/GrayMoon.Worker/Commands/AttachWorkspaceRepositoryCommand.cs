using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Makes the Workspace root folder itself a Git working tree for <see cref="AttachWorkspaceRepositoryRequest.CloneUrl"/>.
/// An empty root is cloned into in place. A non-empty root without Git is initialized, given an origin remote and
/// checked out on the remote default branch (or left on an unborn <c>main</c> when the remote is empty). A root that
/// already has a matching origin is left alone. Never writes <c>.graymoon.json</c> or <c>.gitignore</c> and never commits.
/// </summary>
public sealed class AttachWorkspaceRepositoryCommand(IGitService git)
    : ICommandHandler<AttachWorkspaceRepositoryRequest, AttachWorkspaceRepositoryResponse>
{
    private const string FallbackUnbornBranch = "main";

    public async Task<AttachWorkspaceRepositoryResponse> ExecuteAsync(AttachWorkspaceRepositoryRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var cloneUrl = request.CloneUrl ?? throw new ArgumentException("cloneUrl required");
        var workspaceRoot = request.WorkspaceRoot ?? throw new ArgumentException("workspaceRoot required");
        var bearerToken = request.BearerToken;

        var path = git.GetWorkspacePath(workspaceRoot, workspaceName);
        git.CreateDirectory(path);

        if (WorkerRepositoryPaths.HasGitMetadata(path))
        {
            var origin = await git.GetRemoteOriginUrlAsync(path, cancellationToken);
            if (string.IsNullOrWhiteSpace(origin) || !RepositoryUrlIdentity.RepositoryUrlsEqual(origin, cloneUrl))
            {
                return Fail("Root already has a different Git repository");
            }

            await FinishAsync(request, path, cancellationToken);
            return await SuccessAsync(path, cancellationToken);
        }

        if (!Directory.EnumerateFileSystemEntries(path).Any())
        {
            var cloned = await git.CloneIntoAsync(path, cloneUrl, bearerToken, cancellationToken);
            if (!cloned)
            {
                return Fail("Git clone failed.");
            }

            await FinishAsync(request, path, cancellationToken);
            return await SuccessAsync(path, cancellationToken);
        }

        var (initOk, initError) = await git.InitAsync(path, cancellationToken);
        if (!initOk)
            return Fail(initError ?? "Git init failed.");

        var (remoteOk, remoteError) = await git.AddRemoteAsync(path, "origin", cloneUrl, cancellationToken);
        if (!remoteOk)
            return Fail(remoteError ?? "Git remote add failed.");

        var (fetchOk, fetchError) = await git.FetchAsync(path, includeTags: true, bearerToken, cancellationToken);
        if (!fetchOk)
            return Fail(fetchError ?? "Git fetch failed.");

        // A remote that is empty can still name an unborn HEAD branch; only a branch the fetch actually brought
        // down counts as "the default exists".
        var defaultBranch = await git.GetRemoteDefaultBranchAsync(path, bearerToken, cancellationToken);
        if (!string.IsNullOrWhiteSpace(defaultBranch)
            && await git.RevParseAsync(path, $"refs/remotes/origin/{defaultBranch}", cancellationToken) is null)
        {
            defaultBranch = null;
        }

        if (!string.IsNullOrWhiteSpace(defaultBranch))
        {
            // Git refuses when a tracked file would overwrite an untracked one; its message is returned
            // verbatim and .git stays in place so the user can resolve the collision and retry.
            var (checkoutOk, checkoutError) = await git.CheckoutTrackingAsync(path, defaultBranch, cancellationToken);
            if (!checkoutOk)
                return Fail(checkoutError ?? "Git checkout failed.");
        }
        else
        {
            var (headOk, headError) = await git.SetUnbornHeadAsync(path, FallbackUnbornBranch, cancellationToken);
            if (!headOk)
                return Fail(headError ?? "Git symbolic-ref failed.");
        }

        await FinishAsync(request, path, cancellationToken);
        return await SuccessAsync(path, cancellationToken);
    }

    private async Task FinishAsync(AttachWorkspaceRepositoryRequest request, string path, CancellationToken cancellationToken)
    {
        await git.AddSafeDirectoryAsync(path, cancellationToken);
        await git.WriteSyncHooksAsync(path, request.WorkspaceId, request.RepositoryId, cancellationToken);
    }

    private async Task<AttachWorkspaceRepositoryResponse> SuccessAsync(string path, CancellationToken cancellationToken)
    {
        var branch = await git.GetCurrentBranchNameAsync(path, cancellationToken);
        var head = await git.GetHeadCommitAsync(path, cancellationToken);
        return new AttachWorkspaceRepositoryResponse
        {
            Success = true,
            Branch = string.IsNullOrWhiteSpace(branch) ? null : branch,
            IsUnborn = string.IsNullOrWhiteSpace(head),
        };
    }

    private static AttachWorkspaceRepositoryResponse Fail(string message) =>
        new() { Success = false, ErrorMessage = message };
}
