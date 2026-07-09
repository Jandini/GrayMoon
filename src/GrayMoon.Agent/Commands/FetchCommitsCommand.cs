using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

/// <summary>
/// Runs git fetch (with tags) + commit counts per repo. Skips GitVersion, csproj scan,
/// branch listing, and hook writing. Used by the Quick Fetch workspace action.
/// </summary>
public sealed class FetchCommitsCommand(IGitService git) : ICommandHandler<FetchCommitsRequest, FetchCommitsResponse>
{
    public async Task<FetchCommitsResponse> ExecuteAsync(FetchCommitsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        if (!git.DirectoryExists(repoPath))
            return new FetchCommitsResponse { Success = false, ErrorMessage = "Repository not cloned yet." };

        await git.AddSafeDirectoryAsync(repoPath, cancellationToken);

        var (fetchOk, fetchErr) = await git.FetchAsync(repoPath, includeTags: true, request.BearerToken, cancellationToken);
        if (!fetchOk)
            return new FetchCommitsResponse { Success = false, ErrorMessage = fetchErr ?? "Git fetch failed." };

        var currentTag = await git.GetCheckedOutTagAsync(repoPath, cancellationToken);
        var tags = await git.GetTagsAsync(repoPath, cancellationToken);

        int? outgoing = null;
        int? incoming = null;
        bool? hasUpstream = null;
        int? defaultBehind = null;
        int? defaultAhead = null;

        if (currentTag == null)
        {
            var branch = await git.GetCurrentBranchNameAsync(repoPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(branch))
            {
                var defaultRef = await git.GetDefaultBranchOriginRefAsync(repoPath, cancellationToken);
                var countsTask = git.GetCommitCountsAsync(repoPath, branch, defaultRef, cancellationToken);
                var vsDefaultTask = git.GetCommitCountsVsDefaultAsync(repoPath, defaultRef, cancellationToken);
                await Task.WhenAll(countsTask, vsDefaultTask);
                (outgoing, incoming, var upstream) = await countsTask;
                (defaultBehind, defaultAhead, _) = await vsDefaultTask;
                hasUpstream = upstream;
            }
        }

        return new FetchCommitsResponse
        {
            Success = true,
            OutgoingCommits = outgoing,
            IncomingCommits = incoming,
            HasUpstream = hasUpstream,
            DefaultBranchBehind = defaultBehind,
            DefaultBranchAhead = defaultAhead,
            Tags = tags.Count > 0 ? tags : null,
            CurrentTag = currentTag
        };
    }
}
