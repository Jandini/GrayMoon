using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Synchronizes one repository in three stages: a common git snapshot that always runs, then optional
/// version enrichment, then optional .NET project enrichment. The two optional stages are activated by the
/// request's capabilities; when one is skipped its result is absent from the response rather than empty, so
/// the app can tell "nobody looked" from "there is nothing there" and leaves that group of columns alone.
/// </summary>
public sealed class SyncRepositoryCommand(
    IGitService git,
    ICsProjFileService csProjFileService,
    IRepositoryVersionProviderFactory versionProviderFactory) : ICommandHandler<SyncRepositoryRequest, SyncRepositoryResponse>
{
    public async Task<SyncRepositoryResponse> ExecuteAsync(SyncRepositoryRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryId = request.RepositoryId;
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var cloneUrl = request.CloneUrl;
        var bearerToken = request.BearerToken;
        var workspaceId = request.WorkspaceId;
        var capabilities = request.EffectiveCapabilities;

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

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
        bool? hasUpstream = null;
        var upstreamProbed = false;
        string? versionError = null;
        string? fetchError = null;
        if (git.DirectoryExists(repoPath))
        {
            await git.AddSafeDirectoryAsync(repoPath, cancellationToken);

            // Stage 1, part one of the common git snapshot: the fetch has to complete before any version
            // provider runs, because GitVersion is invoked with /nofetch.
            var (fetchOk, fetchErr) = await git.FetchAsync(repoPath, includeTags: true, bearerToken, cancellationToken);
            fetchError = fetchErr;
            if (!fetchOk)
            {
                return new SyncRepositoryResponse
                {
                    Success = false,
                    ErrorMessage = fetchError ?? "Git fetch failed.",
                    Version = version,
                    Branch = branch,
                    Projects = projects,
                    OutgoingCommits = outgoingCommits,
                    IncomingCommits = incomingCommits,
                    GitVersionError = versionError,
                    GitFetchError = fetchError
                };
            }

            // Stage 2: optional version enrichment. It is resolved here, ahead of the rest of the common
            // snapshot, because the provider's own branch name wins over git's when it produced one and the
            // commit counts below are taken against whichever name wins.
            var versionResult = await versionProviderFactory
                .Create(capabilities)
                .GetVersionAsync(repoPath, RepositoryVersionOptions.Default, cancellationToken);
            versionError = versionResult.Error;
            if (versionResult.Probed)
                version = versionResult.VersionOrPlaceholder;

            // Stage 1, part two: identity, refs, divergence and upstream. Always runs, for every profile.
            // The version provider may fail (an empty repository has no commits for it to read, a path over the
            // Windows limit breaks it) or be switched off entirely. Neither may cost the repository its
            // identity: the branch is a plain git fact.
            branch = await git.ResolveBranchAsync(versionResult.Result, repoPath, cancellationToken) ?? "-";

            // Detect tag/detached HEAD; if on a tag we don't have a real branch so wipe the version branch echo.
            var currentTag = await git.GetCheckedOutTagAsync(repoPath, cancellationToken);
            // Always fetch the full tag list - fetch already ran with includeTags:true so local refs are current.
            var allTags = await git.GetTagsAsync(repoPath, cancellationToken);
            if (currentTag != null)
            {
                branch = "-";
            }

            // Tag checkouts need current hooks too: hooks are shared with linked Feature worktrees, and a
            // stale static-path hook attributes every Feature worktree event to the special Workspace.
            // Only a valid checkout is required: a workspace that does not version its repositories resolves
            // no version, and gating on one would leave it with no managed hooks at all.
            if (branch != "-" || currentTag != null)
                await git.WriteSyncHooksAsync(repoPath, workspaceId, repositoryId, cancellationToken);

            // Resolve default branch once; run commit counts and divergence in parallel when we have a branch.
            // Divergence may be vs Feature parent (request / persisted) rather than the repository default.
            var defaultRef = await git.GetDefaultBranchOriginRefAsync(repoPath, cancellationToken);
            await git.SetDivergenceBaseBranchAsync(repoPath, request.DivergenceBaseBranch, cancellationToken);
            var divergenceRef = git.ToOriginBranchRef(request.DivergenceBaseBranch) ?? defaultRef;
            int? defaultBehind = null;
            int? defaultAhead = null;
            string? defaultBranch = defaultRef != null
                ? (defaultRef.StartsWith("origin/", StringComparison.Ordinal)
                    ? defaultRef["origin/".Length..]
                    : defaultRef)
                : null;

            if (branch != "-")
            {
                var countsTask = git.ProbeCommitCountsAsync(repoPath, branch, defaultRef, cancellationToken);
                var vsDefaultTask = git.GetCommitCountsVsDefaultAsync(repoPath, divergenceRef, cancellationToken);
                await Task.WhenAll(countsTask, vsDefaultTask);
                var counts = await countsTask;
                (defaultBehind, defaultAhead, _) = await vsDefaultTask;
                outgoingCommits = counts.Outgoing;
                incomingCommits = counts.Incoming;
                // Sync is the flow users reach for when a row looks wrong, so it has to report the upstream
                // flag too. Leaving it out meant a stale "no upstream" survived every sync.
                hasUpstream = counts.HasUpstream;
                upstreamProbed = counts.UpstreamProbed;
            }
            else if (divergenceRef != null)
            {
                (defaultBehind, defaultAhead, _) = await git.GetCommitCountsVsDefaultAsync(repoPath, divergenceRef, cancellationToken);
            }

            // Branch lists from local refs (no extra network after fetch)
            IReadOnlyList<string>? localBranches = null;
            IReadOnlyList<string>? remoteBranches = null;
            try
            {
                var localBranchesTask = git.GetLocalBranchesAsync(repoPath, cancellationToken);
                var remoteBranchesTask = git.GetRemoteBranchesFromRefsAsync(repoPath, cancellationToken);
                localBranches = await localBranchesTask;
                remoteBranches = await remoteBranchesTask;
            }
            catch
            {
                // If branch fetching fails, continue without branches (non-critical)
            }

            // Stage 3: optional project enrichment. Left null when the profile does not discover .NET
            // projects - an empty list would tell the app this repository genuinely has none, and it would
            // prune every persisted project row.
            var discoverProjects = request.EffectiveCapabilities.ShouldDiscoverProjects
                && !WorkerRepositoryPaths.IsWorkspaceRepository(repositoryName, request.WorkspaceRepositoryName);
            if (discoverProjects)
                projects = await csProjFileService.FindAsync(repoPath, cancellationToken);

            return new SyncRepositoryResponse
            {
                Success = true,
                Version = version,
                Branch = branch,
                Tag = currentTag,
                Tags = allTags,
                Projects = projects,
                OutgoingCommits = outgoingCommits,
                IncomingCommits = incomingCommits,
                LocalBranches = localBranches,
                RemoteBranches = remoteBranches,
                DefaultBranch = defaultBranch,
                GitVersionError = versionError,
                GitFetchError = fetchError,
                DefaultBranchBehind = defaultBehind,
                DefaultBranchAhead = defaultAhead,
                HasUpstream = hasUpstream,
                UpstreamProbed = upstreamProbed
            };
        }

        return new SyncRepositoryResponse
        {
            Success = true,
            Version = version,
            Branch = branch,
            Projects = projects,
            OutgoingCommits = outgoingCommits,
            IncomingCommits = incomingCommits,
            GitVersionError = versionError,
            GitFetchError = fetchError
        };
    }
}
