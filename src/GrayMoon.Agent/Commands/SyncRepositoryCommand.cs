using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Commands;

public sealed class SyncRepositoryCommand(IGitService git, ICsProjFileService csProjFileService) : ICommandHandler<SyncRepositoryRequest, SyncRepositoryResponse>
{
    public async Task<SyncRepositoryResponse> ExecuteAsync(SyncRepositoryRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryId = request.RepositoryId;
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var cloneUrl = request.CloneUrl;
        var bearerToken = request.BearerToken;
        var workspaceId = request.WorkspaceId;

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        git.CreateDirectory(workspacePath);

        if (!git.DirectoryExists(repoPath) && !string.IsNullOrWhiteSpace(cloneUrl))
        {
            var ok = await git.CloneAsync(workspacePath, cloneUrl, bearerToken, cancellationToken);
            if (ok)
                await git.AddSafeDirectoryAsync(repoPath, cancellationToken);
        }

        var version = "-";
        var branch = "-";
        IReadOnlyList<CsProjFileInfo>? projects = null;
        int? outgoingCommits = null;
        int? incomingCommits = null;
        string? versionError = null;
        if (git.DirectoryExists(repoPath))
        {
            await git.AddSafeDirectoryAsync(repoPath, cancellationToken);
            // FindAsync only reads .csproj files; safe to run in parallel with git operations.
            var findProjectsTask = csProjFileService.FindAsync(repoPath, cancellationToken);

            GitVersionResult? vr;
            (vr, versionError) = await git.GetVersionAsync(repoPath, cancellationToken);
            if (vr != null)
            {
                version = vr.SemVer ?? vr.FullSemVer ?? "-";
                branch = vr.BranchName ?? vr.EscapedBranchName ?? "-";
            }

            // Fetch and WriteSyncHooks touch different .git subdirs (refs/objects vs hooks); safe in parallel.
            var fetchTask = git.FetchAsync(repoPath, includeTags: true, bearerToken, cancellationToken);
            if (version != "-" && branch != "-")
                git.WriteSyncHooks(repoPath, workspaceId, repositoryId);
            await fetchTask;

            if (branch != "-")
            {
                var (outgoing, incoming, _) = await git.GetCommitCountsAsync(repoPath, branch, cancellationToken);
                outgoingCommits = outgoing;
                incomingCommits = incoming;
            }

            // Fetch branches after fetch completes (branches are now up to date)
            IReadOnlyList<string>? localBranches = null;
            IReadOnlyList<string>? remoteBranches = null;
            string? defaultBranch = null;
            try
            {
                var localBranchesTask = git.GetLocalBranchesAsync(repoPath, cancellationToken);
                var remoteBranchesTask = git.GetRemoteBranchesAsync(repoPath, cancellationToken);
                var defaultBranchTask = git.GetDefaultBranchNameAsync(repoPath, cancellationToken);
                localBranches = await localBranchesTask;
                remoteBranches = await remoteBranchesTask;
                defaultBranch = await defaultBranchTask;
            }
            catch
            {
                // If branch fetching fails, continue without branches (non-critical)
            }

            projects = await findProjectsTask;

            return new SyncRepositoryResponse
            {
                Version = version,
                Branch = branch,
                Projects = projects,
                OutgoingCommits = outgoingCommits,
                IncomingCommits = incomingCommits,
                LocalBranches = localBranches,
                RemoteBranches = remoteBranches,
                DefaultBranch = defaultBranch,
                GitVersionError = versionError
            };
        }

        return new SyncRepositoryResponse { Version = version, Branch = branch, Projects = projects, OutgoingCommits = outgoingCommits, IncomingCommits = incomingCommits, GitVersionError = versionError };
    }
}
