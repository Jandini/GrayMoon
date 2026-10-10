using System.Diagnostics;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Synchronizes one repository in three stages: a common git snapshot that always runs, then optional
/// version enrichment, then optional .NET project enrichment. The two optional stages are activated by the
/// request's capabilities; when one is skipped its result is absent from the response rather than empty, so
/// the app can tell "nobody looked" from "there is nothing there" and leaves that group of columns alone.
/// After the fetch, the version provider (which holds the repository write lock), the read lane, and the project
/// scan run side by side; the branch-dependent steps run once the first two finish. The read lane is one
/// in-process local snapshot (<see cref="ILocalGitSnapshotReader"/>: refs, HEAD, tags, default branch, upstream
/// and ahead/behind, no git process); when that cannot read the repository it falls back, logged, to the
/// read-intent git CLI reads. Fetch, origin/HEAD repair, hooks and the version provider stay on the git CLI.
/// A Debug line per repository reports where the time went (fetch, version, read lane and its steps, project
/// scan, hooks and counts).
/// </summary>
public sealed class SyncRepositoryCommand(
    IGitService git, IGitRepositoryReader reader,
    ILocalGitSnapshotReader snapshotReader,
    ICsProjFileService csProjFileService,
    IRepositoryVersionProviderFactory versionProviderFactory,
    ILogger<SyncRepositoryCommand>? logger = null,
    IGitChangesRefreshSuppressor? refreshSuppressor = null,
    IRepositoryConfigurationInitializer? configuration = null) : ICommandHandler<SyncRepositoryRequest, SyncRepositoryResponse>
{
    /// <summary>Where the read lane's answers came from.</summary>
    private enum RefsSource
    {
        /// <summary>The in-process local snapshot.</summary>
        Snapshot,

        /// <summary>The git CLI reads (the snapshot failed, or the environment points git elsewhere).</summary>
        GitCli,

        /// <summary>Nothing: the folder is not a repository of its own.</summary>
        None,
    }

    /// <summary>Ref reads that do not depend on GitVersion's output or on the branch name.</summary>
    /// <param name="CurrentBranchKnown">The current branch was read (so no separate read is needed after the version provider).</param>
    /// <param name="CountsBranch">The branch <paramref name="CurrentBranchCounts"/> was taken for.</param>
    /// <param name="CurrentBranchCounts">Upstream/outgoing counts for the checked-out branch, when the snapshot took them.</param>
    private sealed record SyncRefs(
        string? CurrentBranch,
        bool CurrentBranchKnown,
        string? CurrentTag,
        IReadOnlyList<string> Tags,
        IReadOnlyList<string>? LocalBranches,
        IReadOnlyList<string>? RemoteBranches,
        string? DefaultRef,
        int? DefaultBehind,
        int? DefaultAhead,
        bool OriginHeadUnresolved,
        string? CountsBranch,
        CommitCountsProbeResult? CurrentBranchCounts,
        RefsSource Source,
        long ElapsedMs,
        string StepTimings);

    public async Task<SyncRepositoryResponse> ExecuteAsync(SyncRepositoryRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryId = request.RepositoryId;
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var cloneUrl = request.CloneUrl;
        var bearerToken = request.BearerToken;
        var workspaceId = request.WorkspaceId;
        var capabilities = request.EffectiveCapabilities;

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        Directory.CreateDirectory(workspacePath);

        var clonedNow = false;
        if (!Directory.Exists(repoPath) && !string.IsNullOrWhiteSpace(cloneUrl))
        {
            var ok = await git.CloneAsync(workspacePath, cloneUrl, bearerToken, cancellationToken);
            if (ok)
            {
                await git.AddSafeDirectoryAsync(repoPath, cancellationToken);
                clonedNow = Directory.Exists(repoPath);
            }
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
        if (Directory.Exists(repoPath))
        {
            // Fetch rewrites refs under .git, which the Git Changes watchers see as repository changes. Hold
            // their per-event status refreshes back for the whole sync and let the scope's end schedule the one
            // refresh; released on every exit (failed fetch, exception, cancellation) when this block ends.
            using var refreshScope = refreshSuppressor?.BeginExternalRepositoryMutation(repoPath);

            var totalStart = Stopwatch.GetTimestamp();
            await git.AddSafeDirectoryAsync(repoPath, cancellationToken);

            // Onboarding seam for clone, restore and every existing (including pre-existing) repository: the Windows
            // long-path policy is applied once per process and path, before any pinned checkout or Feature work.
            configuration?.EnsureWindowsLongPaths(repoPath, cancellationToken);

            // The fetch has to complete before the version provider and before any ref read: GitVersion is
            // invoked with /nofetch, and the ref reads below must see the fetched refs. A failed fetch starts
            // nothing else (no version, no reads, no project scan).
            var fetchStart = Stopwatch.GetTimestamp();
            var (fetchOk, fetchErr) = await git.FetchAsync(repoPath, includeTags: true, bearerToken, cancellationToken);
            var fetchMs = ElapsedMs(fetchStart);
            fetchError = fetchErr;
            if (!fetchOk)
            {
                logger?.LogDebug("SyncRepository timings for {RepoPath}: fetch={FetchMs}ms (failed), total={TotalMs}ms",
                    repoPath, fetchMs, ElapsedMs(totalStart));
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

            if (clonedNow && request.ApplyRepositoryTagPin)
            {
                var pinError = await CheckoutPinnedCommitAsync(workspacePath, repoPath, cloneUrl, cancellationToken);
                if (pinError is not null)
                {
                    return new SyncRepositoryResponse
                    {
                        Success = false,
                        ErrorMessage = pinError,
                        Version = version,
                        Branch = branch
                    };
                }
            }

            // Three things overlap from here. The version provider (GitVersion) holds the write lock for its
            // process; the read lane takes no lock, so it does not wait for it: an in-process snapshot, or the
            // read-intent git CLI reads as one sequential lane when the snapshot cannot be taken. Project
            // discovery is a file system walk. Stage 3 only discovers when the profile asks for it: otherwise
            // projects stay null - an empty list would tell the app this repository genuinely has none, and it
            // would prune every persisted project row.
            //
            // The current branch is only a fallback for when the version provider gives no branch. The snapshot
            // always has it; on the CLI lane, when a version provider is going to run, it is read after the
            // overlap and only if it is needed, and when none will run it joins the lane.
            var readBranchInLane = !capabilities.ShouldCalculateVersion;
            var overlapStart = Stopwatch.GetTimestamp();
            var versionTask = TimedAsync(() => versionProviderFactory
                .Create(capabilities)
                .GetVersionAsync(repoPath, RepositoryVersionOptions.Default, cancellationToken));
            var refsTask = ReadRefsAsync(repoPath, request.DivergenceBaseBranch, readBranchInLane, cancellationToken);
            // The Workspace repository's working tree is the Workspace root, which contains the nested source
            // repositories, so scanning it would attribute their projects to it.
            var discoverProjects = capabilities.ShouldDiscoverProjects
                && !WorkerRepositoryPaths.IsWorkspaceRepository(repositoryName, request.WorkspaceRepositoryName);
            var projectsTask = discoverProjects
                ? TimedAsync(() => ScanProjectsAsync(repoPath, cancellationToken))
                : Task.FromResult<(IReadOnlyList<CsProjFileInfo>? Value, long Ms)>((null, 0));

            await Task.WhenAll(versionTask, refsTask);
            var overlapMs = ElapsedMs(overlapStart);
            var (versionResult, versionMs) = await versionTask;
            var refs = await refsTask;

            // Stage 2: optional version enrichment.
            versionError = versionResult.Error;
            if (versionResult.Probed)
                version = versionResult.VersionOrPlaceholder;

            // The version provider may fail (an empty repository has no commits for it to read, a path over the
            // Windows limit breaks it) or be switched off entirely. Neither may cost the repository its
            // identity: the branch is a plain git fact. The provider's own branch name wins when it produced
            // one; otherwise the name is read from git.
            var gitBranch = refs.CurrentBranch;
            var branchFallbackMs = 0L;
            if (!refs.CurrentBranchKnown && string.IsNullOrWhiteSpace(GitVersionBranch.Choose(versionResult.Result, null)))
            {
                var fallbackStart = Stopwatch.GetTimestamp();
                gitBranch = await reader.GetCurrentBranchNameAsync(repoPath, cancellationToken);
                branchFallbackMs = ElapsedMs(fallbackStart);
            }

            branch = GitVersionBranch.Choose(versionResult.Result, gitBranch) ?? "-";

            // Detect tag/detached HEAD; if on a tag we don't have a real branch so wipe the version branch echo.
            string? headCommit = null;
            if (refs.CurrentTag != null)
            {
                branch = "-";
                headCommit = await reader.GetHeadCommitAsync(repoPath, cancellationToken);
                if (!WorkspaceDefinitionTagPin.TryNormalize(refs.CurrentTag, headCommit, repositoryName, out _, out headCommit, out var pinError))
                {
                    return new SyncRepositoryResponse
                    {
                        Success = false,
                        ErrorMessage = pinError,
                        Version = version,
                        Branch = branch,
                        Tag = refs.CurrentTag
                    };
                }
            }

            // The default branch is known by now; if it came out of a missing or dangling origin/HEAD, repoint
            // that before the counts below are taken against it.
            var headRepairMs = 0L;
            if (refs.OriginHeadUnresolved)
            {
                var repairStart = Stopwatch.GetTimestamp();
                refs = await RepairOriginHeadAsync(refs, repoPath, request.DivergenceBaseBranch, bearerToken, cancellationToken);
                headRepairMs = ElapsedMs(repairStart);
            }

            // Tag checkouts need current hooks too: hooks are shared with linked Feature worktrees, and a
            // stale static-path hook attributes every Feature worktree event to the special Workspace.
            // Only a valid checkout is required: a workspace that does not version its repositories resolves
            // no version, and gating on one would leave it with no managed hooks at all.
            var tailStart = Stopwatch.GetTimestamp();
            var hooksTask = branch != "-" || refs.CurrentTag != null
                ? TimedAsync(async () => { await git.WriteSyncHooksAsync(repoPath, workspaceId, repositoryId, cancellationToken); return 0; })
                : null;

            // Counts are taken against whichever branch name won. When that is the checked-out branch the snapshot
            // already took them (and took them again after a repair); any other name - a version provider can
            // report a branch that is not checked out - is counted against HEAD by the CLI probe, which reads
            // back the divergence base file the read lane wrote.
            var countsFromSnapshot = branch != "-"
                && refs.CurrentBranchCounts != null
                && string.Equals(branch, refs.CountsBranch, StringComparison.Ordinal);
            var countsTask = branch == "-"
                ? null
                : countsFromSnapshot
                    ? Task.FromResult((refs.CurrentBranchCounts!, 0L))
                    : TimedAsync(() => reader.ProbeCommitCountsAsync(repoPath, branch, refs.DefaultRef, cancellationToken));

            var hooksMs = hooksTask != null ? (await hooksTask).Ms : 0;
            var countsMs = 0L;
            if (countsTask != null)
            {
                var (counts, ms) = await countsTask;
                countsMs = ms;
                outgoingCommits = counts.Outgoing;
                incomingCommits = counts.Incoming;
                // Sync is the flow users reach for when a row looks wrong, so it has to report the upstream
                // flag too. Leaving it out meant a stale "no upstream" survived every sync.
                hasUpstream = counts.HasUpstream;
                upstreamProbed = counts.UpstreamProbed;
            }

            var tailMs = ElapsedMs(tailStart);

            // Stage 3: optional project enrichment (null when not discovered).
            var (scannedProjects, projectsMs) = await projectsTask;
            projects = scannedProjects;

            string? defaultBranch = refs.DefaultRef != null
                ? (refs.DefaultRef.StartsWith("origin/", StringComparison.Ordinal)
                    ? refs.DefaultRef["origin/".Length..]
                    : refs.DefaultRef)
                : null;

            // Where the time went. "overlap" is how long the fetch-to-branch-known stretch took (the longer of
            // version and lane); "tail" is hooks plus counts after it. Compare version and lane to see which one
            // the overlap waited for.
            logger?.LogDebug(
                "SyncRepository timings for {RepoPath}: fetch={FetchMs}ms, overlap={OverlapMs}ms (version={VersionMs}ms, lane={LaneMs}ms [{LaneSource}], projects={ProjectsMs}ms), " +
                "branchFallback={BranchFallbackMs}ms, headRepair={HeadRepairMs}ms, tail={TailMs}ms (hooks={HooksMs}ms, counts={CountsMs}ms [{CountsSource}]), total={TotalMs}ms. Lane steps: {LaneSteps}",
                repoPath, fetchMs, overlapMs, versionMs, refs.ElapsedMs, refs.Source, projectsMs,
                branchFallbackMs, headRepairMs, tailMs, hooksMs, countsMs, countsTask == null ? "none" : countsFromSnapshot ? "snapshot" : "git", ElapsedMs(totalStart), refs.StepTimings);
            return new SyncRepositoryResponse
            {
                Success = true,
                Version = version,
                Branch = branch,
                Tag = refs.CurrentTag,
                Commit = headCommit,
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
    /// A repository just cloned into a Workspace that has a Workspace repository is checked out to the commit
    /// recorded for it when that entry is on a tag. A missing definition file means there is no pin. An invalid
    /// pin or a missing commit fails the sync and removes the clone so the next sync tries again.
    /// </summary>
    private async Task<string?> CheckoutPinnedCommitAsync(string workspacePath, string repoPath, string? cloneUrl, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(workspacePath, ".graymoon.json");
        var definition = File.Exists(manifestPath)
            ? await File.ReadAllTextAsync(manifestPath, cancellationToken)
            : null;
        var (commit, error) = WorkspaceDefinitionTagPin.ReadCommit(definition, cloneUrl);
        if (error is not null)
        {
            RemoveFailedClone(workspacePath, repoPath);
            return error;
        }

        if (commit is null)
            return null;

        var (success, checkoutError) = await git.CheckoutCommitAsync(repoPath, commit, cancellationToken);
        if (success)
            return null;

        RemoveFailedClone(workspacePath, repoPath);
        return checkoutError ?? $"Commit '{commit}' could not be checked out.";
    }

    private void RemoveFailedClone(string workspacePath, string repoPath)
    {
        var clone = Path.GetFullPath(repoPath);
        var workspace = Path.GetFullPath(workspacePath);
        if (string.Equals(clone, workspace, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            if (!Directory.Exists(clone))
                return;

            // Git writes object files read-only, which makes Directory.Delete fail on Windows and would leave
            // the clone in place. The next sync would then see the folder and would not retry the pin.
            foreach (var file in Directory.EnumerateFiles(clone, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(clone, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not remove the clone after a tag commit checkout failed. Path={RepoPath}", clone);
        }
    }

    /// <summary>
    /// Ref reads that need neither GitVersion's output nor the branch name, taken without the repository lock so
    /// they overlap GitVersion, which holds it. Must start only after fetch has finished. Writes the divergence
    /// base file first (worktree-private GrayMoon state that hooks and later commands read back), then takes the
    /// in-process snapshot. When the snapshot cannot read the repository the git CLI reads answer instead - the
    /// one sanctioned fallback, logged as a warning and reported in the timing line. A folder without its own
    /// <c>.git</c> is not read at all: git would walk up and answer for an enclosing repository.
    /// </summary>
    private async Task<SyncRefs> ReadRefsAsync(string repoPath, string? divergenceBaseBranch, bool readCurrentBranch, CancellationToken ct)
    {
        var laneStart = Stopwatch.GetTimestamp();
        var steps = new List<string>(8);
        var lap = Stopwatch.GetTimestamp();
        void Lap(string name)
        {
            steps.Add($"{name}={ElapsedMs(lap)}ms");
            lap = Stopwatch.GetTimestamp();
        }

        await git.SetDivergenceBaseBranchAsync(repoPath, divergenceBaseBranch, ct);
        Lap("divergenceBase");

        // libgit2 does not honour GIT_DIR; git does, so a Worker started with it keeps the CLI reads.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_DIR")))
        {
            if (!Directory.Exists(Path.Combine(repoPath, ".git")) && !File.Exists(Path.Combine(repoPath, ".git")))
            {
                logger?.LogWarning("{RepoPath} has no .git of its own; refs left unread rather than taken from an enclosing repository", repoPath);
                return new SyncRefs(
                    null, true, null, [], null, null, null, null, null, false, null, null,
                    RefsSource.None, ElapsedMs(laneStart), string.Join(", ", steps));
            }

            try
            {
                var snapshot = await ReadSnapshotAsync(repoPath, divergenceBaseBranch, ct);
                Lap($"snapshot(graph={snapshot.GraphCalculations})");
                return new SyncRefs(
                    snapshot.CurrentBranch,
                    true,
                    snapshot.CheckedOutTag,
                    snapshot.Refs.Tags,
                    snapshot.Refs.LocalBranches,
                    snapshot.Refs.RemoteBranches,
                    snapshot.Refs.DefaultOriginRef,
                    snapshot.DefaultBehind,
                    snapshot.DefaultAhead,
                    !snapshot.Refs.OriginHeadResolved,
                    snapshot.CurrentBranch,
                    snapshot.CurrentBranchCounts,
                    RefsSource.Snapshot,
                    ElapsedMs(laneStart),
                    string.Join(", ", steps));
            }
            catch (LocalGitReadException ex)
            {
                Lap("snapshot(failed)");
                logger?.LogWarning(ex, "Local snapshot failed for {RepoPath}; falling back to git CLI reads", repoPath);
            }
        }

        return await ReadRefsWithGitCliAsync(repoPath, divergenceBaseBranch, readCurrentBranch, laneStart, steps, ct);
    }

    /// <summary>
    /// Takes the in-process snapshot off the calling thread (it is synchronous) and logs one Debug record for it.
    /// The repository is opened and disposed inside the call.
    /// </summary>
    private async Task<LocalGitSnapshot> ReadSnapshotAsync(string repoPath, string? divergenceBaseBranch, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var snapshot = await Task.Run(() => snapshotReader.Read(repoPath, new LocalGitSnapshotRequest(divergenceBaseBranch), ct), ct);
        logger?.LogDebug(
            "Local snapshot for {RepoPath}: elapsed={ElapsedMs}ms, branch={Branch}, detached={IsDetached}, unborn={IsUnborn}, localBranches={LocalBranchCount}, " +
            "remoteBranches={RemoteBranchCount}, tags={TagCount}, hasUpstream={HasUpstream}, originHeadResolved={OriginHeadResolved}, graphCalculations={GraphCalculations}",
            repoPath, ElapsedMs(start), snapshot.CurrentBranch, snapshot.CurrentBranch == null, snapshot.IsHeadUnborn, snapshot.Refs.LocalBranches.Count,
            snapshot.Refs.RemoteBranches.Count, snapshot.Refs.Tags.Count, snapshot.CurrentBranchCounts?.HasUpstream, snapshot.Refs.OriginHeadResolved, snapshot.GraphCalculations);
        return snapshot;
    }

    /// <summary>
    /// The git CLI read lane, as read intent so it overlaps GitVersion: the fallback for when the snapshot cannot
    /// read the repository. The current branch is read here only when <paramref name="readCurrentBranch"/> is set
    /// (no version provider will run, so its answer is the only one there is).
    /// </summary>
    private async Task<SyncRefs> ReadRefsWithGitCliAsync(
        string repoPath, string? divergenceBaseBranch, bool readCurrentBranch, long laneStart, List<string> steps, CancellationToken ct)
    {
        var lap = Stopwatch.GetTimestamp();
        void Lap(string name)
        {
            steps.Add($"{name}={ElapsedMs(lap)}ms");
            lap = Stopwatch.GetTimestamp();
        }

        string? currentBranch = null;
        string? currentTag;
        IReadOnlyList<string> tags;
        IReadOnlyList<string>? localBranches = null;
        IReadOnlyList<string>? remoteBranches = null;

        // Tags and both branch lists (fetch already ran with includeTags:true, so local refs are current and there
        // is no extra network) come from one for-each-ref, and the same listing says whether HEAD is attached to
        // a branch. Attached means no tag checkout, so the symbolic-ref and describe calls are not needed, and the
        // branch name is already known.
        var snapshot = await reader.GetRefSnapshotAsync(repoPath, ct);
        Lap("refs");
        if (snapshot != null)
        {
            tags = snapshot.Tags;
            localBranches = snapshot.LocalBranches;
            remoteBranches = snapshot.RemoteBranches;

            if (snapshot.CheckedOutBranch != null)
            {
                currentTag = null;
                if (readCurrentBranch)
                    currentBranch = snapshot.CheckedOutBranch;
            }
            else
            {
                // Detached HEAD, or an unborn branch the listing cannot see: ask git, as before.
                currentTag = await reader.GetCheckedOutTagAsync(repoPath, ct);
                Lap("checkedOutTag");
                if (readCurrentBranch)
                {
                    currentBranch = await reader.GetCurrentBranchNameAsync(repoPath, ct);
                    Lap("branch");
                }
            }
        }
        else
        {
            // The combined listing failed: fall back to the separate reads, which keep their own failure handling.
            if (readCurrentBranch)
            {
                currentBranch = await reader.GetCurrentBranchNameAsync(repoPath, ct);
                Lap("branch");
            }

            currentTag = await reader.GetCheckedOutTagAsync(repoPath, ct);
            Lap("checkedOutTag");
            tags = await reader.GetTagsAsync(repoPath, ct);
            Lap("tags");

            try
            {
                localBranches = await reader.GetLocalBranchesAsync(repoPath, ct);
                remoteBranches = await reader.GetRemoteBranchesFromRefsAsync(repoPath, ct);
            }
            catch
            {
                // If branch fetching fails, continue without branches (non-critical)
            }

            Lap("branchLists");
        }

        // Resolve default branch once. Divergence may be vs Feature parent (request / persisted) rather than
        // the repository default. The listing above already contains what the lookup needs (where origin/HEAD
        // points and which origin branches exist), so no further git call is made when it succeeded; a null
        // there means the repository has no default branch. Only when the listing failed is git asked.
        var defaultRef = snapshot != null
            ? snapshot.DefaultOriginRef
            : await reader.GetDefaultBranchOriginRefAsync(repoPath, ct);
        Lap("defaultBranch");
        var divergenceRef = OriginDefaultRef.ToOriginBranchRef(divergenceBaseBranch) ?? defaultRef;

        int? defaultBehind = null;
        int? defaultAhead = null;
        if (divergenceRef != null)
            (defaultBehind, defaultAhead, _) = await reader.GetCommitCountsVsDefaultAsync(repoPath, divergenceRef, ct);
        Lap("defaultCounts");

        // Only a listing that succeeded can say origin/HEAD is missing or dangling; when it failed nothing is known.
        return new SyncRefs(
            currentBranch, readCurrentBranch, currentTag, tags, localBranches, remoteBranches, defaultRef, defaultBehind, defaultAhead,
            snapshot is { OriginHeadResolved: false },
            null, null, RefsSource.GitCli,
            ElapsedMs(laneStart), string.Join(", ", steps));
    }

    /// <summary>
    /// A fetch never repoints <c>origin/HEAD</c>, so when the remote renames or replaces its default branch it
    /// dangles, and the default branch (and the counts against it) is lost or silently wrong until it is repointed.
    /// This asks the remote and repoints it, then takes the default branch again, and the counts against it when
    /// they were taken against the default (a Feature's divergence base is unaffected). It runs after GitVersion
    /// has finished because it writes a ref and so needs the repository write lock the version provider holds;
    /// the snapshot has finished and released the repository by then. The repair is a git CLI mutation; the
    /// re-read after it is a fresh snapshot when the lane used one (the checked-out branch's no-upstream counts
    /// may have been taken against the old default, so they are taken again too), else the git CLI reads.
    /// Any failure leaves the refs as they were.
    /// </summary>
    private async Task<SyncRefs> RepairOriginHeadAsync(SyncRefs refs, string repoPath, string? divergenceBaseBranch, string? bearerToken, CancellationToken ct)
    {
        if (!await git.RepairOriginHeadAsync(repoPath, bearerToken, ct))
            return refs;

        if (refs.Source == RefsSource.Snapshot)
        {
            try
            {
                var snapshot = await ReadSnapshotAsync(repoPath, divergenceBaseBranch, ct);
                return refs with
                {
                    DefaultRef = snapshot.Refs.DefaultOriginRef,
                    DefaultBehind = snapshot.DefaultBehind,
                    DefaultAhead = snapshot.DefaultAhead,
                    OriginHeadUnresolved = !snapshot.Refs.OriginHeadResolved,
                    CountsBranch = snapshot.CurrentBranch,
                    CurrentBranchCounts = snapshot.CurrentBranchCounts,
                };
            }
            catch (LocalGitReadException ex)
            {
                // The counts may be against the old default: leave them to the CLI probe.
                logger?.LogWarning(ex, "Local snapshot after the origin/HEAD repair failed for {RepoPath}; falling back to git CLI reads", repoPath);
                refs = refs with { CountsBranch = null, CurrentBranchCounts = null };
            }
        }

        var defaultRef = await reader.GetDefaultBranchOriginRefAsync(repoPath, ct);
        if (defaultRef == refs.DefaultRef)
            return refs;

        var behind = refs.DefaultBehind;
        var ahead = refs.DefaultAhead;
        if (OriginDefaultRef.ToOriginBranchRef(divergenceBaseBranch) == null)
        {
            behind = null;
            ahead = null;
            if (defaultRef != null)
                (behind, ahead, _) = await reader.GetCommitCountsVsDefaultAsync(repoPath, defaultRef, ct);
        }

        return refs with { DefaultRef = defaultRef, DefaultBehind = behind, DefaultAhead = ahead };
    }

    /// <summary>Runs the project scan off the calling thread so its directory walk does not block the sync.</summary>
    private Task<IReadOnlyList<CsProjFileInfo>?> ScanProjectsAsync(string repoPath, CancellationToken ct)
        => Task.Run<IReadOnlyList<CsProjFileInfo>?>(async () =>
        {
            try
            {
                return await csProjFileService.FindAsync(repoPath, ct);
            }
            catch (ProjectDiscoveryException ex)
            {
                // Not probed (null), never an empty list: an empty list would prune every persisted project row.
                logger?.LogWarning(ex, "Project discovery failed for {RepoPath}; projects left unprobed", repoPath);
                return null;
            }
        }, ct);

    /// <summary>Starts <paramref name="work"/> right away and reports how long it took alongside its result.</summary>
    private static async Task<(T Value, long Ms)> TimedAsync<T>(Func<Task<T>> work)
    {
        var start = Stopwatch.GetTimestamp();
        var value = await work();
        return (value, ElapsedMs(start));
    }

    private static long ElapsedMs(long startTimestamp) => (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
