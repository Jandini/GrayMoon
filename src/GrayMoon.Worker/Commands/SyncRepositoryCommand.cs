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
/// After the fetch, the version provider (which holds the repository write lock), one lane of read-intent ref
/// reads, and the project scan run side by side; the branch-dependent steps run once the first two finish.
/// </summary>
public sealed class SyncRepositoryCommand(
    IGitService git,
    ICsProjFileService csProjFileService,
    IRepositoryVersionProviderFactory versionProviderFactory) : ICommandHandler<SyncRepositoryRequest, SyncRepositoryResponse>
{
    /// <summary>Ref reads that do not depend on GitVersion's output or on the branch name.</summary>
    private sealed record SyncRefs(
        string? CurrentBranch,
        string? CurrentTag,
        IReadOnlyList<string> Tags,
        IReadOnlyList<string>? LocalBranches,
        IReadOnlyList<string>? RemoteBranches,
        string? DefaultRef,
        int? DefaultBehind,
        int? DefaultAhead);

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
        bool? hasUpstream = null;
        var upstreamProbed = false;
        string? versionError = null;
        string? fetchError = null;
        if (git.DirectoryExists(repoPath))
        {
            await git.AddSafeDirectoryAsync(repoPath, cancellationToken);

            // The fetch has to complete before the version provider and before any ref read: GitVersion is
            // invoked with /nofetch, and the ref reads below must see the fetched refs. A failed fetch starts
            // nothing else (no version, no reads, no project scan).
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

            // Three things overlap from here. The version provider (GitVersion) holds the write lock for its
            // process; the ref reads run as read intent so they do not wait for it, and they are one sequential
            // lane to avoid adding many concurrent git processes per repository. Project discovery is a file
            // system walk and has nothing to do with git. Stage 3 only discovers when the profile asks for it:
            // otherwise projects stay null - an empty list would tell the app this repository genuinely has none,
            // and it would prune every persisted project row.
            var versionTask = versionProviderFactory
                .Create(capabilities)
                .GetVersionAsync(repoPath, RepositoryVersionOptions.Default, cancellationToken);
            var refsTask = ReadRefsAsync(repoPath, request.DivergenceBaseBranch, cancellationToken);
            var projectsTask = capabilities.ShouldDiscoverProjects
                ? ScanProjectsAsync(repoPath, cancellationToken)
                : Task.FromResult<IReadOnlyList<CsProjFileInfo>?>(null);

            await Task.WhenAll(versionTask, refsTask);
            var versionResult = await versionTask;
            var refs = await refsTask;

            // Stage 2: optional version enrichment.
            versionError = versionResult.Error;
            if (versionResult.Probed)
                version = versionResult.VersionOrPlaceholder;

            // The version provider may fail (an empty repository has no commits for it to read, a path over the
            // Windows limit breaks it) or be switched off entirely. Neither may cost the repository its
            // identity: the branch is a plain git fact. The provider's own branch name wins when it produced
            // one; otherwise the name read from git while the provider ran is used.
            branch = GitVersionBranch.Choose(versionResult.Result, refs.CurrentBranch) ?? "-";

            // Detect tag/detached HEAD; if on a tag we don't have a real branch so wipe the version branch echo.
            if (refs.CurrentTag != null)
                branch = "-";

            // Tag checkouts need current hooks too: hooks are shared with linked Feature worktrees, and a
            // stale static-path hook attributes every Feature worktree event to the special Workspace.
            // Only a valid checkout is required: a workspace that does not version its repositories resolves
            // no version, and gating on one would leave it with no managed hooks at all.
            var hooksTask = branch != "-" || refs.CurrentTag != null
                ? git.WriteSyncHooksAsync(repoPath, workspaceId, repositoryId, cancellationToken)
                : Task.CompletedTask;

            // Counts are taken against whichever branch name won. The divergence base file was already written
            // in the read lane, which the no-upstream path of the probe reads back.
            var countsTask = branch != "-"
                ? git.ProbeCommitCountsAsync(repoPath, branch, refs.DefaultRef, cancellationToken, intent: GitLockIntent.Read)
                : null;

            await hooksTask;
            if (countsTask != null)
            {
                var counts = await countsTask;
                outgoingCommits = counts.Outgoing;
                incomingCommits = counts.Incoming;
                // Sync is the flow users reach for when a row looks wrong, so it has to report the upstream
                // flag too. Leaving it out meant a stale "no upstream" survived every sync.
                hasUpstream = counts.HasUpstream;
                upstreamProbed = counts.UpstreamProbed;
            }

            // Stage 3: optional project enrichment (null when not discovered).
            projects = await projectsTask;

            string? defaultBranch = refs.DefaultRef != null
                ? (refs.DefaultRef.StartsWith("origin/", StringComparison.Ordinal)
                    ? refs.DefaultRef["origin/".Length..]
                    : refs.DefaultRef)
                : null;

            return new SyncRepositoryResponse
            {
                Success = true,
                Version = version,
                Branch = branch,
                Tag = refs.CurrentTag,
                Tags = refs.Tags,
                Projects = projects,
                OutgoingCommits = outgoingCommits,
                IncomingCommits = incomingCommits,
                LocalBranches = refs.LocalBranches,
                RemoteBranches = refs.RemoteBranches,
                DefaultBranch = defaultBranch,
                GitVersionError = versionError,
                GitFetchError = fetchError,
                DefaultBranchBehind = refs.DefaultBehind,
                DefaultBranchAhead = refs.DefaultAhead,
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

    /// <summary>
    /// Ref reads that need neither GitVersion's output nor the branch name, run as read intent so they can
    /// overlap GitVersion, which holds the repository write lock. Must start only after fetch has finished.
    /// </summary>
    private async Task<SyncRefs> ReadRefsAsync(string repoPath, string? divergenceBaseBranch, CancellationToken ct)
    {
        const GitLockIntent read = GitLockIntent.Read;

        // Only a fallback: the branch from the version provider wins when it produced one.
        var currentBranch = await git.GetCurrentBranchNameAsync(repoPath, ct, read);
        var currentTag = await git.GetCheckedOutTagAsync(repoPath, ct, read);
        // Always fetch the full tag list - fetch already ran with includeTags:true so local refs are current.
        var tags = await git.GetTagsAsync(repoPath, ct, read);

        // Branch lists from local refs (no extra network after fetch)
        IReadOnlyList<string>? localBranches = null;
        IReadOnlyList<string>? remoteBranches = null;
        try
        {
            localBranches = await git.GetLocalBranchesAsync(repoPath, ct, read);
            remoteBranches = await git.GetRemoteBranchesFromRefsAsync(repoPath, ct, read);
        }
        catch
        {
            // If branch fetching fails, continue without branches (non-critical)
        }

        // Resolve default branch once. Divergence may be vs Feature parent (request / persisted) rather than
        // the repository default.
        var defaultRef = await git.GetDefaultBranchOriginRefAsync(repoPath, ct, read);
        await git.SetDivergenceBaseBranchAsync(repoPath, divergenceBaseBranch, ct);
        var divergenceRef = git.ToOriginBranchRef(divergenceBaseBranch) ?? defaultRef;

        int? defaultBehind = null;
        int? defaultAhead = null;
        if (divergenceRef != null)
            (defaultBehind, defaultAhead, _) = await git.GetCommitCountsVsDefaultAsync(repoPath, divergenceRef, ct, read);

        return new SyncRefs(currentBranch, currentTag, tags, localBranches, remoteBranches, defaultRef, defaultBehind, defaultAhead);
    }

    /// <summary>Runs the project scan off the calling thread so its directory walk does not block the sync.</summary>
    private Task<IReadOnlyList<CsProjFileInfo>?> ScanProjectsAsync(string repoPath, CancellationToken ct)
        => Task.Run<IReadOnlyList<CsProjFileInfo>?>(async () => await csProjFileService.FindAsync(repoPath, ct), ct);
}
