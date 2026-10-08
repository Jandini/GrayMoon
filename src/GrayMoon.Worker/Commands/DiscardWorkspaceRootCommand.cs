using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Removes the Workspace root a failed restore created. Deletes only an empty folder or a clean clone whose origin
/// is the restored repository; anything else (other files, another repository, local changes) is left untouched and
/// reported, so a rollback can never delete work GrayMoon did not create.
/// </summary>
public sealed class DiscardWorkspaceRootCommand(GitProcessRunner runner, IGitRepositoryReader reader)
    : ICommandHandler<DiscardWorkspaceRootRequest, DiscardWorkspaceRootResponse>
{
    internal const string ForeignFilesReason = "The folder contains files that GrayMoon did not create.";
    internal const string DifferentRepositoryReason = "The folder holds a different Git repository.";
    internal const string LocalChangesReason = "The folder has changes that GrayMoon did not make.";
    internal const string StatusFailedReason = "GrayMoon could not check the folder for changes.";
    internal const string ResidueReason = "Some files could not be deleted. They may be open in another program.";

    public async Task<DiscardWorkspaceRootResponse> ExecuteAsync(DiscardWorkspaceRootRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var workspaceRoot = request.WorkspaceRoot ?? throw new ArgumentException("workspaceRoot required");
        var cloneUrl = request.CloneUrl ?? throw new ArgumentException("cloneUrl required");

        var path = WorkerRepositoryPaths.GetWorkspacePath(workspaceRoot, workspaceName);
        if (!Directory.Exists(path))
            return Removed();

        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            if (!WorkerRepositoryPaths.HasGitMetadata(path))
                return Kept(ForeignFilesReason);

            var origin = await reader.GetRemoteOriginUrlAsync(path, cancellationToken);
            if (string.IsNullOrWhiteSpace(origin) || !RepositoryUrlIdentity.RepositoryUrlsEqual(origin, cloneUrl))
                return Kept(DifferentRepositoryReason);

            var (exitCode, stdout, _) = await runner.RunAsync(
                "git", "--no-optional-locks status --porcelain --untracked-files=all", path, cancellationToken, intent: GitLockIntent.Read);
            if (exitCode != 0)
                return Kept(StatusFailedReason);
            if (!string.IsNullOrWhiteSpace(stdout))
                return Kept(LocalChangesReason);

            await GitWorktreeService.DeleteFolderRecursivelyWithRetryAsync(path, cancellationToken);
            if (Directory.EnumerateFileSystemEntries(path).Any())
                return Kept(ResidueReason);
        }

        if (!request.KeepFolder)
        {
            try
            {
                Directory.Delete(path, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Kept(ResidueReason);
            }
        }

        return Removed();
    }

    private static DiscardWorkspaceRootResponse Removed() => new() { Removed = true };

    private static DiscardWorkspaceRootResponse Kept(string reason) => new() { Removed = false, Reason = reason };
}
