using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// The remote-only observation of one repository: git fetch (pruning, with tags), then what the fetch can change -
/// commit counts against the upstream and the divergence base, the tags, and the local and remote branch lists.
/// It runs no GitVersion and no project scan, writes no hooks and never moves HEAD. Used by the Quick Fetch
/// workspace action and by the refresh the Feature removal queue runs before a removal.
/// <para>
/// After the fetch the local facts come from one in-process snapshot (<see cref="ILocalGitSnapshotReader"/>, no
/// git process), the same read SyncRepository uses, so a Sync that follows reports the same counts. When the
/// snapshot cannot be used (it failed, the Worker runs with GIT_DIR, origin/HEAD is missing or dangling) the git
/// CLI reads answer instead.
/// </para>
/// </summary>
public sealed class FetchCommitsCommand(
    IGitService git,
    IGitRepositoryReader reader,
    ILocalGitSnapshotReader? snapshotReader = null,
    ILogger<FetchCommitsCommand>? logger = null) : ICommandHandler<FetchCommitsRequest, FetchCommitsResponse>
{
    public async Task<FetchCommitsResponse> ExecuteAsync(FetchCommitsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        if (!Directory.Exists(repoPath))
            return new FetchCommitsResponse { Success = false, ErrorMessage = "Repository not cloned yet." };

        await git.AddSafeDirectoryAsync(repoPath, cancellationToken);

        var (fetchOk, fetchErr) = await git.FetchAsync(repoPath, includeTags: true, request.BearerToken, cancellationToken);
        if (!fetchOk)
            return new FetchCommitsResponse { Success = false, ErrorMessage = fetchErr ?? "Git fetch failed." };

        // Worktree-private GrayMoon state that the counts below, the hooks and later commands read back.
        await git.SetDivergenceBaseBranchAsync(repoPath, request.DivergenceBaseBranch, cancellationToken);

        return await TryReadFromSnapshotAsync(repoPath, request.DivergenceBaseBranch, cancellationToken)
            ?? await ReadWithGitCliAsync(repoPath, request.DivergenceBaseBranch, cancellationToken);
    }

    /// <summary>
    /// The in-process read. Null means "use the git CLI reads": no snapshot reader, GIT_DIR is set (libgit2 does
    /// not honour it), the folder has no <c>.git</c> of its own, the read failed, or origin/HEAD does not resolve
    /// (the default branch would be a guess; only a Sync repairs it).
    /// </summary>
    private async Task<FetchCommitsResponse?> TryReadFromSnapshotAsync(string repoPath, string? divergenceBaseBranch, CancellationToken ct)
    {
        if (snapshotReader is null || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_DIR")))
            return null;
        if (!Directory.Exists(Path.Combine(repoPath, ".git")) && !File.Exists(Path.Combine(repoPath, ".git")))
            return null;

        LocalGitSnapshot snapshot;
        try
        {
            snapshot = await Task.Run(() => snapshotReader.Read(repoPath, new LocalGitSnapshotRequest(divergenceBaseBranch), ct), ct);
        }
        catch (LocalGitReadException ex)
        {
            logger?.LogWarning(ex, "Local snapshot failed for {RepoPath}; falling back to git CLI reads", repoPath);
            return null;
        }

        if (!snapshot.Refs.OriginHeadResolved)
            return null;

        var response = new FetchCommitsResponse
        {
            Success = true,
            Tags = snapshot.Refs.Tags,
            CurrentTag = snapshot.CheckedOutTag,
            LocalBranches = snapshot.Refs.LocalBranches,
            RemoteBranches = snapshot.Refs.RemoteBranches,
            DefaultBranch = ToBranchName(snapshot.Refs.DefaultOriginRef),
            BranchesProbed = true,
        };

        // On a tag, detached, or with a branch that has no commit yet there is nothing to count.
        if (snapshot.CheckedOutTag != null || string.IsNullOrWhiteSpace(snapshot.CurrentBranch) || snapshot.CurrentBranchCounts is not { } counts)
            return response;

        response.OutgoingCommits = counts.Outgoing;
        response.IncomingCommits = counts.Incoming;
        response.HasUpstream = counts.UpstreamProbed ? counts.HasUpstream : null;
        response.DefaultBranchBehind = snapshot.DefaultBehind;
        response.DefaultBranchAhead = snapshot.DefaultAhead;
        response.CommitCountsProbed = counts.CountsProbed;
        return response;
    }

    private async Task<FetchCommitsResponse> ReadWithGitCliAsync(string repoPath, string? divergenceBaseBranch, CancellationToken cancellationToken)
    {
        var currentTag = await reader.GetCheckedOutTagAsync(repoPath, cancellationToken);

        // One listing answers the tags and both branch lists; the single-purpose tag read is the fallback when it fails.
        var refs = await reader.GetRefSnapshotAsync(repoPath, cancellationToken);
        var tags = refs?.Tags ?? await reader.GetTagsAsync(repoPath, cancellationToken);

        int? outgoing = null;
        int? incoming = null;
        bool? hasUpstream = null;
        int? defaultBehind = null;
        int? defaultAhead = null;
        var countsProbed = false;
        string? defaultRef = null;

        if (currentTag == null)
        {
            var branch = await reader.GetCurrentBranchNameAsync(repoPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(branch))
            {
                defaultRef = await reader.GetDefaultBranchOriginRefAsync(repoPath, cancellationToken);
                var divergenceRef = OriginDefaultRef.ToOriginBranchRef(divergenceBaseBranch) ?? defaultRef;
                var countsTask = reader.ProbeCommitCountsAsync(repoPath, branch, defaultRef, cancellationToken);
                var vsDefaultTask = reader.GetCommitCountsVsDefaultAsync(repoPath, divergenceRef, cancellationToken);
                await Task.WhenAll(countsTask, vsDefaultTask);
                var counts = await countsTask;
                outgoing = counts.Outgoing;
                incoming = counts.Incoming;
                hasUpstream = counts.UpstreamProbed ? counts.HasUpstream : null;
                countsProbed = counts.CountsProbed;
                (defaultBehind, defaultAhead, _) = await vsDefaultTask;
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
            CommitCountsProbed = countsProbed,
            Tags = tags,
            CurrentTag = currentTag,
            LocalBranches = refs?.LocalBranches,
            RemoteBranches = refs?.RemoteBranches,
            DefaultBranch = ToBranchName(refs?.DefaultOriginRef ?? defaultRef),
            BranchesProbed = refs != null,
        };
    }

    private static string? ToBranchName(string? originRef)
        => string.IsNullOrWhiteSpace(originRef)
            ? null
            : originRef.StartsWith("origin/", StringComparison.Ordinal) ? originRef["origin/".Length..] : originRef;
}
