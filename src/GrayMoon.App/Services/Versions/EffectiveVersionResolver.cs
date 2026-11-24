using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Jobs;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Application.Repositories.Policy;
using GrayMoon.Application.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.Versions;

/// <summary>The effective version decision for one repository in one context.</summary>
public sealed record EffectiveVersionDecision(
    int RepositoryId,
    int WorkspaceRepositoryId,
    string RepositoryName,
    EffectiveVersionKind Kind,
    string? EffectiveVersion,
    string? CalculatedVersion,
    string? ResolvedForCommitSha,
    string ReasonCode,
    string? Message);

/// <summary>The decisions of one <see cref="EffectiveVersionResolver.ResolveAsync"/> run.</summary>
public sealed record EffectiveVersionResolution(IReadOnlyList<EffectiveVersionDecision> Decisions)
{
    public static EffectiveVersionResolution Empty { get; } = new([]);

    public IReadOnlyList<EffectiveVersionDecision> Unresolved
        => Decisions.Where(d => d.Kind == EffectiveVersionKind.Unresolved).ToList();

    /// <summary>The one message an operation stops with when a repository in scope is unresolved (D3), or null.</summary>
    public string? StopMessage
    {
        get
        {
            var unresolved = Unresolved;
            return unresolved.Count == 0
                ? null
                : string.Join(Environment.NewLine, unresolved.Select(d => d.Message ?? $"Cannot verify the published packages of {d.RepositoryName}. Nothing was changed."));
        }
    }
}

/// <summary>
/// Decides the effective version of repositories in one context (design "Effective Package Versions", section 6.1) and
/// writes it through <see cref="WorkspaceRepositoryStateWriter.WriteEffectiveVersionAsync"/>. A repository inherits the
/// published version of its parent branch only when its HEAD is the recorded branch point, that commit is on
/// <c>origin/&lt;parent&gt;</c>, and every package a consumer in the context requires is available at the parent version on
/// its mapped connector. A repository that only version-file <c>{@Repo}</c> tokens use has no package to look up and
/// inherits on the first two conditions alone (<see cref="EffectiveVersionKind.InheritedUnverified"/>). A confirmed
/// missing package gives the repository its own version; any other registry answer leaves it unresolved. A read only
/// snapshot of a Feature is detached and has no branch point: its effective version is the version of the branch it was
/// taken from at its commit (<see cref="EffectiveVersionReasonCodes.ReferenceSnapshot"/>), without a registry lookup;
/// when that cannot be calculated and something uses its version, it is unresolved. The Worker is
/// asked only for repositories that are on their branch point and have a version consumer, the registry only for those
/// with required packages; no database transaction is open meanwhile.
/// </summary>
/// <remarks>Scoped. It reads and writes through the scope's <see cref="AppDbContext"/>, so a caller never runs two
/// resolutions on one scope at once; work that is not on a page's own flow resolves its own scope.</remarks>
public sealed class EffectiveVersionResolver(
    AppDbContext dbContext,
    WorkspaceRepositoryStateWriter stateWriter,
    IWorkerBridge workerBridge,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceCapabilitiesResolver capabilitiesResolver,
    IWorkspaceRepositoryOperationPolicy operationPolicy,
    PackageAvailabilityVerifier verifier,
    PackageRegistrySyncService registrySync,
    IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<EffectiveVersionResolver> logger)
{
    /// <summary>
    /// Resolves <paramref name="repositoryIds"/> (all repositories of the Workspace when null) in
    /// <paramref name="contextId"/>. <paramref name="bypassCache"/> asks the registry even for versions it confirmed a
    /// moment ago; the start of an operation always sets it. Even then a parent version stored for the current HEAD (an
    /// inherited decision) is reused instead of running GitVersion again, since it does not change for that commit: only the
    /// registry is asked again. Without it (the refresh after every state write, which runs
    /// in the hook sync queue that operations wait for), a repository whose stored decision came from a branch point check
    /// for its current HEAD and the same kind of consumers keeps that decision: the Worker and the registry are not asked
    /// again. Cancellation writes nothing. With <paramref name="progress"/> (the start check of an operation) each finished
    /// branch point check is counted (<see cref="VerifiedMessage"/>), and every decision is written as one line to the
    /// job's overlay terminal (<see cref="TerminalLine"/>).
    /// </summary>
    public async Task<EffectiveVersionResolution> ResolveAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int>? repositoryIds,
        bool bypassCache,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var isSpecialWorkspace = await dbContext.WorkspaceFeatureContexts
            .AsNoTracking()
            .Where(c => c.WorkspaceFeatureContextId == contextId.Value && c.WorkspaceId == workspaceId)
            .Select(c => (bool?)(c.Kind == WorkspaceFeatureContextKind.Workspace))
            .FirstOrDefaultAsync(cancellationToken);
        if (isSpecialWorkspace is null)
            return EffectiveVersionResolution.Empty;

        var links = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId && (repositoryIds == null || repositoryIds.Contains(wr.RepositoryId)))
            .Select(wr => new { wr.WorkspaceRepositoryId, wr.RepositoryId, Name = wr.Repository!.RepositoryName })
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
            return EffectiveVersionResolution.Empty;

        var linkIds = links.Select(l => l.WorkspaceRepositoryId).ToList();
        var states = await dbContext.WorkspaceRepositoryContextStates
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == contextId.Value && linkIds.Contains(s.WorkspaceRepositoryId))
            .Select(s => new StoredState(s.WorkspaceRepositoryId, s.HeadCommit, s.GitVersion, s.BranchName, s.EffectiveVersion, s.EffectiveVersionKind, s.EffectiveVersionCommitSha, s.EffectiveVersionReasonCode))
            .ToDictionaryAsync(s => s.WorkspaceRepositoryId, cancellationToken);

        // Without its own state row a repository has nothing to compare and nothing to write to (I6).
        var candidates = links
            .Where(l => states.ContainsKey(l.WorkspaceRepositoryId))
            .Select(l => new Candidate(l.RepositoryId, l.WorkspaceRepositoryId, l.Name ?? $"#{l.RepositoryId}", states[l.WorkspaceRepositoryId].HeadCommit, states[l.WorkspaceRepositoryId].GitVersion))
            .ToList();
        if (candidates.Count == 0)
            return EffectiveVersionResolution.Empty;

        var branchByLinkId = states.ToDictionary(s => s.Key, s => s.Value.BranchName);
        var baselines = await RepositoryBranchBaselineReader.LoadAsync(dbContext, contextId.Value, isSpecialWorkspace.Value, branchByLinkId, cancellationToken);
        var snapshotSourceBranches = isSpecialWorkspace.Value
            ? new Dictionary<int, string>()
            : await RepositoryBranchBaselineReader.LoadReferenceSnapshotSourceBranchesAsync(dbContext, contextId.Value, linkIds, cancellationToken);
        var consumers = await VersionConsumerReader.LoadAsync(dbContext, workspaceId, contextId.Value, isSpecialWorkspace.Value, cancellationToken);
        var allowed = (await operationPolicy.PartitionAsync(
            workspaceId, contextId, RepositoryOperation.Push, candidates.Select(c => c.RepositoryId).ToList(), RepositorySubjectSources.PushPlan, cancellationToken))
            .Eligible.ToHashSet();
        var capabilities = await capabilitiesResolver.GetAsync(workspaceId, cancellationToken);

        var decisions = new Dictionary<int, EffectiveVersionDecision>();
        var probes = new List<Probe>();
        foreach (var candidate in candidates)
        {
            baselines.TryGetValue(candidate.WorkspaceRepositoryId, out var baseline);
            var consumer = consumers.GetValueOrDefault(candidate.RepositoryId, VersionConsumers.None);
            if (snapshotSourceBranches.TryGetValue(candidate.WorkspaceRepositoryId, out var sourceBranch))
            {
                // A read only snapshot is detached, and GitVersion on a detached HEAD does not give the version its
                // branch published (a commit after a tag reads as the tag). Its version is the source branch's at its
                // commit, with or without a consumer, so the grid shows it too.
                if (!capabilities.UsesGitVersion || string.IsNullOrWhiteSpace(candidate.HeadCommit))
                    decisions[candidate.RepositoryId] = Own(candidate, EffectiveVersionReasonCodes.PolicyProtected, null);
                else if (!bypassCache && StoredSnapshotDecision(candidate, states[candidate.WorkspaceRepositoryId], consumer.HasVersionConsumer) is { } storedSnapshot)
                    decisions[candidate.RepositoryId] = storedSnapshot;
                else
                    probes.Add(new Probe(
                        candidate, new BranchBaseline(sourceBranch, candidate.HeadCommit.Trim()), [],
                        StoredParentVersion(candidate, states[candidate.WorkspaceRepositoryId]),
                        IsReferenceSnapshot: true, HasVersionConsumer: consumer.HasVersionConsumer));
                continue;
            }

            // Without NuGet packages in the profile there is nothing to look up; a token consumer still needs a version.
            IReadOnlyList<RequiredPackage> required = capabilities.UsesNuGetPackages ? consumer.RequiredPackages : [];
            var reason = !allowed.Contains(candidate.RepositoryId) ? EffectiveVersionReasonCodes.PolicyProtected
                : baseline is null ? EffectiveVersionReasonCodes.NoBaseline
                : !string.Equals(candidate.HeadCommit?.Trim(), baseline.BaseCommitSha, StringComparison.OrdinalIgnoreCase) ? EffectiveVersionReasonCodes.NotOnBaseline
                : required.Count == 0 && !consumer.HasTokenConsumer ? EffectiveVersionReasonCodes.NoRequiredPackages
                : !capabilities.UsesGitVersion ? EffectiveVersionReasonCodes.ParentVersionUnavailable
                : null;
            if (reason != null)
                decisions[candidate.RepositoryId] = Own(candidate, reason, null);
            else if (!bypassCache && StoredProbeDecision(candidate, states[candidate.WorkspaceRepositoryId], required.Count > 0) is { } stored)
                decisions[candidate.RepositoryId] = stored;
            else
                probes.Add(new Probe(candidate, baseline!, required, StoredParentVersion(candidate, states[candidate.WorkspaceRepositoryId])));
        }

        if (probes.Count > 0)
        {
            foreach (var decision in await ProbeAsync(workspaceId, contextId, probes, bypassCache, progress, cancellationToken))
                decisions[decision.RepositoryId] = decision;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var ordered = candidates.Select(c => decisions[c.RepositoryId]).ToList();
        await stateWriter.WriteEffectiveVersionAsync(
            contextId,
            ordered.Select(d => new EffectiveVersionWrite(d.WorkspaceRepositoryId, d.Kind, d.EffectiveVersion, d.ResolvedForCommitSha, d.ReasonCode)).ToList(),
            cancellationToken);

        if (progress != null && TerminalSinkContext.Current is { } terminal)
        {
            foreach (var decision in ordered)
                terminal.Append(TerminalLabel, decision.Kind == EffectiveVersionKind.Unresolved ? WorkerCommandStreamKind.Stderr : WorkerCommandStreamKind.Stdout, TerminalLine(decision));
        }

        foreach (var decision in ordered.Where(d => d.Kind != EffectiveVersionKind.Own || d.ReasonCode is EffectiveVersionReasonCodes.PackageNotPublished))
        {
            logger.LogInformation(
                "Effective version: context {ContextId}, repository {Repository}, commit {Commit}, calculated {Calculated}, effective {Effective}, kind {Kind}, reason {Reason}",
                contextId.Value, decision.RepositoryName, decision.ResolvedForCommitSha, decision.CalculatedVersion, decision.EffectiveVersion, decision.Kind, decision.ReasonCode);
        }

        logger.LogDebug(
            "Effective versions resolved for workspace {WorkspaceId} context {ContextId}: {Count} repositories, {Probed} checked, {Inherited} inherited, {Unresolved} unresolved in {ElapsedMs} ms",
            workspaceId, contextId.Value, ordered.Count, probes.Count,
            ordered.Count(d => d.Kind is EffectiveVersionKind.InheritedVerified or EffectiveVersionKind.InheritedUnverified),
            ordered.Count(d => d.Kind == EffectiveVersionKind.Unresolved),
            stopwatch.ElapsedMilliseconds);
        return new EffectiveVersionResolution(ordered);
    }

    private async Task<List<EffectiveVersionDecision>> ProbeAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        List<Probe> probes,
        bool bypassCache,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var results = new List<EffectiveVersionDecision>();

        // 1. Parent version at the branch point, per repository. A version stored for this same HEAD is reused: the
        //    parent version at a fixed commit does not change, so only the registry is asked again. The rest run
        //    GitVersion on the Worker (bounded) and are counted.
        var parentVersions = new ConcurrentDictionary<int, BranchPointResult>();
        foreach (var probe in probes.Where(p => p.KnownParentVersion != null))
            parentVersions[probe.Candidate.RepositoryId] = new BranchPointResult(probe.KnownParentVersion, OnRemoteParent: true, null);

        var toCalculate = probes.Where(p => p.KnownParentVersion == null).ToList();
        if (toCalculate.Count > 0)
        {
            var workerArgs = await pathResolver.GetWorkerArgsAsync(contextId, cancellationToken);
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, workspaceOptions.Value.MaxParallelOperations),
                CancellationToken = cancellationToken,
            };
            var checkedCount = 0;
            await Parallel.ForEachAsync(toCalculate, options, async (probe, ct) =>
            {
                parentVersions[probe.Candidate.RepositoryId] = await GetParentVersionAsync(workerArgs, probe.Candidate.RepositoryName, probe.Baseline, ct);
                var done = Interlocked.Increment(ref checkedCount);
                progress.Report(CheckedMessage(done, toCalculate.Count), done, toCalculate.Count);
            });
        }

        logger.LogDebug(
            "Effective version: {Reused} parent versions reused for an unchanged commit, {Calculated} calculated by the Worker",
            probes.Count - toCalculate.Count, toCalculate.Count);

        var lookups = new List<(Candidate Candidate, string ParentVersion, IReadOnlyList<RequiredPackage> Required)>();
        foreach (var probe in probes)
        {
            var (candidate, baseline, required) = (probe.Candidate, probe.Baseline, probe.Required);
            var parent = parentVersions[candidate.RepositoryId];
            if (probe.IsReferenceSnapshot)
                results.Add(DecideSnapshot(candidate, baseline, parent, probe.HasVersionConsumer));
            else if (parent.OnRemoteParent == false)
                results.Add(Own(candidate, EffectiveVersionReasonCodes.BaselineNotOnRemoteParent, null));
            else if (string.IsNullOrWhiteSpace(parent.Version))
            {
                logger.LogInformation(
                    "Effective version: no parent version for {Repository} ({Parent} at {Commit}): {Error}",
                    candidate.RepositoryName, baseline.ParentBranchName, baseline.BaseCommitSha, parent.Error);
                results.Add(Own(candidate, EffectiveVersionReasonCodes.ParentVersionUnavailable, null));
            }
            else if (required.Count == 0)
            {
                // Only version-file tokens use it: there is no package to look up, so its code being the parent's is enough.
                results.Add(new EffectiveVersionDecision(
                    candidate.RepositoryId, candidate.WorkspaceRepositoryId, candidate.RepositoryName,
                    EffectiveVersionKind.InheritedUnverified, parent.Version!.Trim(), candidate.GitVersion, candidate.HeadCommit,
                    EffectiveVersionReasonCodes.TokenConsumersOnly, null));
            }
            else
                lookups.Add((candidate, parent.Version!.Trim(), required));
        }

        if (lookups.Count == 0)
            return results;

        // 2. A package without a registry mapping is matched first; still unmatched after a sync that answered means
        //    no registry has it (missing). A sync that failed says nothing (unknown).
        var unmapped = lookups.SelectMany(l => l.Required).Where(r => r.MatchedConnectorId is null).Select(r => r.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappingBySync = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var syncAnswered = true;
        if (unmapped.Count > 0)
        {
            syncAnswered = await registrySync.SyncRegistriesForPackageIdsAsync(workspaceId, unmapped, cancellationToken);
            var rows = await dbContext.WorkspaceProjects
                .AsNoTracking()
                .Where(p => p.WorkspaceId == workspaceId && p.MatchedConnectorId != null)
                .Select(p => new { p.PackageId, p.ProjectName, p.MatchedConnectorId })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                var id = (!string.IsNullOrWhiteSpace(row.PackageId) ? row.PackageId : row.ProjectName)?.Trim();
                if (!string.IsNullOrEmpty(id) && unmapped.Contains(id))
                    mappingBySync.TryAdd(id, row.MatchedConnectorId);
            }
        }

        var connectorIds = lookups.SelectMany(l => l.Required).Select(r => r.MatchedConnectorId)
            .Concat(mappingBySync.Values)
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var connectors = await dbContext.Connectors
            .AsNoTracking()
            .Where(c => connectorIds.Contains(c.ConnectorId))
            .ToDictionaryAsync(c => c.ConnectorId, cancellationToken);

        // 3. One lookup per (connector, package, parent version), deduplicated across repositories.
        var plan = new List<(Candidate Candidate, string ParentVersion, List<(RequiredPackage Package, PackageLookupRequest? Request, string? Problem)> Packages)>();
        foreach (var (candidate, parentVersion, required) in lookups)
        {
            var packages = new List<(RequiredPackage, PackageLookupRequest?, string?)>();
            foreach (var package in required)
            {
                var connectorId = package.MatchedConnectorId ?? mappingBySync.GetValueOrDefault(package.PackageId);
                if (connectorId is null)
                    packages.Add((package, null, syncAnswered ? EffectiveVersionReasonCodes.PackageNotPublished : EffectiveVersionReasonCodes.RegistryFailure));
                else if (!connectors.TryGetValue(connectorId.Value, out var connector) || !connector.IsActive || !ConnectorHelpers.IsPackageRegistry(connector.ConnectorType))
                    packages.Add((package, null, EffectiveVersionReasonCodes.RegistryFailure));
                else
                    packages.Add((package, new PackageLookupRequest(connector, package.PackageId, parentVersion), null));
            }

            plan.Add((candidate, parentVersion, packages));
        }

        var outcomes = await verifier.VerifyAsync(
            plan.SelectMany(p => p.Packages).Where(p => p.Request != null).Select(p => p.Request!),
            bypassCache,
            cancellationToken);

        foreach (var (candidate, parentVersion, packages) in plan)
            results.Add(Decide(candidate, parentVersion, packages, outcomes));

        return results;
    }

    /// <summary>
    /// The decision for a read only snapshot of a Feature: the version of the branch it was taken from at its commit.
    /// When that version cannot be calculated there is no right version to hand to a consumer, so with a version
    /// consumer the snapshot is unresolved and operations stop; without one nothing reads it and it keeps its own.
    /// </summary>
    private EffectiveVersionDecision DecideSnapshot(Candidate candidate, BranchBaseline source, BranchPointResult sourceVersion, bool hasVersionConsumer)
    {
        if (sourceVersion.OnRemoteParent != false && !string.IsNullOrWhiteSpace(sourceVersion.Version))
        {
            return new EffectiveVersionDecision(
                candidate.RepositoryId, candidate.WorkspaceRepositoryId, candidate.RepositoryName,
                EffectiveVersionKind.InheritedUnverified, sourceVersion.Version.Trim(), candidate.GitVersion, candidate.HeadCommit,
                EffectiveVersionReasonCodes.ReferenceSnapshot, null);
        }

        var shortSha = source.BaseCommitSha.Length > 7 ? source.BaseCommitSha[..7] : source.BaseCommitSha;
        var notOnRemote = sourceVersion.OnRemoteParent == false;
        var reason = notOnRemote ? EffectiveVersionReasonCodes.BaselineNotOnRemoteParent : EffectiveVersionReasonCodes.ParentVersionUnavailable;
        logger.LogInformation(
            "Effective version: no version for the read only snapshot {Repository} ({Branch} at {Commit}): {Error}",
            candidate.RepositoryName, source.ParentBranchName, source.BaseCommitSha, notOnRemote ? "the commit is not on the remote branch" : sourceVersion.Error);
        if (!hasVersionConsumer)
            return Own(candidate, reason, null);

        var why = notOnRemote
            ? $"its commit {shortSha} is not on origin/{source.ParentBranchName}, so no published version exists for it"
            : $"GitVersion gave no version for {source.ParentBranchName} at {shortSha} ({sourceVersion.Error ?? "no answer"})";
        return Unresolved(candidate, reason,
            $"Cannot calculate the version of the read only snapshot {candidate.RepositoryName}: {why}. Repositories in this Feature use that version. Fix it and run again. Nothing was changed.");
    }

    private EffectiveVersionDecision Decide(
        Candidate candidate,
        string parentVersion,
        List<(RequiredPackage Package, PackageLookupRequest? Request, string? Problem)> packages,
        IReadOnlyDictionary<(int ConnectorId, string PackageId, string Version), PackageLookupOutcome> outcomes)
    {
        // A missing package decides first (D3): the repository must publish its own version.
        foreach (var (package, request, problem) in packages)
        {
            var missing = problem == EffectiveVersionReasonCodes.PackageNotPublished
                || (request != null && outcomes.TryGetValue(request.Key, out var outcome) && outcome.Availability == PackageAvailability.Missing);
            if (missing)
            {
                var message = $"{candidate.RepositoryName}: package {package.PackageId} {parentVersion} is not published; publishing {candidate.GitVersion ?? "its own version"} instead.";
                return Own(candidate, EffectiveVersionReasonCodes.PackageNotPublished, message);
            }
        }

        foreach (var (package, request, problem) in packages)
        {
            if (problem != null)
            {
                var connectorText = request?.Connector.ConnectorName ?? (package.MatchedConnectorId is int id ? $"#{id}" : "(no registry match)");
                return Unresolved(candidate, EffectiveVersionReasonCodes.RegistryFailure,
                    $"Cannot verify the published package {package.PackageId} {parentVersion} for {candidate.RepositoryName}: connector {connectorText} could not be asked. Fix the connector and run again. Nothing was changed.");
            }

            var outcome = outcomes[request!.Key];
            if (outcome.Availability == PackageAvailability.Available)
                continue;

            var (reason, what) = outcome.Kind switch
            {
                PackageVersionLookupKind.AuthenticationFailed => (EffectiveVersionReasonCodes.RegistryAuthenticationFailed, $"returned HTTP {outcome.StatusCode?.ToString() ?? "401/403"}"),
                PackageVersionLookupKind.RequestTimeout => (EffectiveVersionReasonCodes.RegistryTimeout, outcome.StatusCode is int code ? $"returned HTTP {code}" : "did not answer in time"),
                PackageVersionLookupKind.UnexpectedResponse => (EffectiveVersionReasonCodes.RegistryUnexpectedResponse, "returned an unexpected answer"),
                _ => (EffectiveVersionReasonCodes.RegistryFailure, outcome.StatusCode is int failed ? $"returned HTTP {failed}" : $"failed ({outcome.Error ?? outcome.Kind.ToString()})"),
            };
            return Unresolved(candidate, reason,
                $"Cannot verify the published package {package.PackageId} {parentVersion} for {candidate.RepositoryName}: connector {request.Connector.ConnectorName} {what}. Fix the connector and run again. Nothing was changed.");
        }

        return new EffectiveVersionDecision(
            candidate.RepositoryId, candidate.WorkspaceRepositoryId, candidate.RepositoryName,
            EffectiveVersionKind.InheritedVerified, parentVersion, candidate.GitVersion, candidate.HeadCommit,
            EffectiveVersionReasonCodes.Verified, null);
    }

    private async Task<BranchPointResult> GetParentVersionAsync(
        WorkerWorkspaceArgs workerArgs, string repositoryName, BranchBaseline baseline, CancellationToken cancellationToken)
    {
        WorkerCommandResponse response;
        try
        {
            response = await workerBridge.SendCommandAsync(
                WorkerHubMethods.GetGitVersionAtBranchPoint,
                new
                {
                    workspaceName = workerArgs.WorkspaceFolderName,
                    repositoryName,
                    workspaceRoot = workerArgs.WorkspaceRoot,
                    workspaceRepositoryName = workerArgs.WorkspaceRepositoryName,
                    parentBranch = baseline.ParentBranchName,
                    commitSha = baseline.BaseCommitSha,
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // No parent version: the repository uses its own (D9), with the reason in the log.
            logger.LogWarning(ex, "Effective version: the Worker could not calculate the parent version of {Repository}", repositoryName);
            return new BranchPointResult(null, null, ex.Message);
        }

        var payload = response.Data == null ? null : WorkerResponseJson.DeserializeWorkerResponse<BranchPointWorkerResponse>(response.Data);
        if (!response.Success || payload == null)
            return new BranchPointResult(null, null, response.Error ?? "The Worker gave no answer.");
        return payload.Success
            ? new BranchPointResult(payload.Version, payload.BaselineOnRemoteParent, null)
            : new BranchPointResult(null, payload.BaselineOnRemoteParent, payload.ErrorMessage);
    }

    /// <summary>Stream label of the effective version lines in the overlay terminal.</summary>
    internal const string TerminalLabel = "versions";

    /// <summary>
    /// The progress text after each branch point GitVersion run on the Worker, with <see cref="OperationProgress.Completed"/>
    /// and <see cref="OperationProgress.Total"/> set. Parent versions reused for an unchanged commit are not counted.
    /// </summary>
    internal static string CheckedMessage(int done, int total) => $"Checked {done} of {total} repositories...";

    /// <summary>
    /// The parent version stored for the current HEAD: an inherited decision records the parent branch version at the
    /// branch point, which does not change for that commit. Null when nothing valid is stored, and for every other kind
    /// (an own version does not record the parent's), so the Worker calculates it.
    /// </summary>
    private static string? StoredParentVersion(Candidate candidate, StoredState state) =>
        EffectiveVersionRules.IsInherited(state.EffectiveVersionKind, state.EffectiveVersion, state.EffectiveVersionCommitSha, candidate.HeadCommit)
            ? state.EffectiveVersion!.Trim()
            : null;

    /// <summary>One repository whose effective version needs its parent version; <see cref="KnownParentVersion"/> skips the Worker.</summary>
    /// <remarks>For a read only snapshot (<see cref="IsReferenceSnapshot"/>) the baseline is its source branch and its
    /// current commit, nothing is looked up, and <see cref="HasVersionConsumer"/> decides what a failure means.</remarks>
    private sealed record Probe(
        Candidate Candidate,
        BranchBaseline Baseline,
        IReadOnlyList<RequiredPackage> Required,
        string? KnownParentVersion,
        bool IsReferenceSnapshot = false,
        bool HasVersionConsumer = false);

    /// <summary>One overlay terminal line for a decision: the version dependents use and why.</summary>
    internal static string TerminalLine(EffectiveVersionDecision decision) => decision.Kind switch
    {
        EffectiveVersionKind.InheritedVerified =>
            $"{decision.RepositoryName}: uses the published parent version {decision.EffectiveVersion} ({EffectiveVersionReasonCodes.Describe(decision.ReasonCode)})",
        EffectiveVersionKind.InheritedUnverified =>
            $"{decision.RepositoryName}: uses the parent version {decision.EffectiveVersion} ({EffectiveVersionReasonCodes.Describe(decision.ReasonCode)})",
        EffectiveVersionKind.Unresolved when decision.ReasonCode is EffectiveVersionReasonCodes.BaselineNotOnRemoteParent or EffectiveVersionReasonCodes.ParentVersionUnavailable =>
            $"{decision.RepositoryName}: cannot calculate its version ({decision.Message ?? EffectiveVersionReasonCodes.Describe(decision.ReasonCode)})",
        EffectiveVersionKind.Unresolved =>
            $"{decision.RepositoryName}: cannot verify its packages ({decision.Message ?? EffectiveVersionReasonCodes.Describe(decision.ReasonCode)})",
        _ => string.IsNullOrWhiteSpace(decision.EffectiveVersion)
            ? $"{decision.RepositoryName}: own version ({EffectiveVersionReasonCodes.Describe(decision.ReasonCode)})"
            : $"{decision.RepositoryName}: own version {decision.EffectiveVersion} ({EffectiveVersionReasonCodes.Describe(decision.ReasonCode)})",
    };

    private static EffectiveVersionDecision Own(Candidate candidate, string reasonCode, string? message) => new(
        candidate.RepositoryId, candidate.WorkspaceRepositoryId, candidate.RepositoryName,
        EffectiveVersionKind.Own, candidate.GitVersion, candidate.GitVersion, candidate.HeadCommit, reasonCode, message);

    private static EffectiveVersionDecision Unresolved(Candidate candidate, string reasonCode, string message) => new(
        candidate.RepositoryId, candidate.WorkspaceRepositoryId, candidate.RepositoryName,
        EffectiveVersionKind.Unresolved, null, candidate.GitVersion, candidate.HeadCommit, reasonCode, message);

    /// <summary>
    /// The stored decision of a repository that is on its branch point, when it came from a branch point check of its
    /// current HEAD for the same kind of consumers (packages to verify, or version-file tokens only), else null. Unresolved
    /// is never reused, so a registry that recovers is noticed on the next refresh.
    /// </summary>
    private static EffectiveVersionDecision? StoredProbeDecision(Candidate candidate, StoredState state, bool hasRequiredPackages)
    {
        if (string.IsNullOrWhiteSpace(candidate.HeadCommit)
            || !string.Equals(state.EffectiveVersionCommitSha?.Trim(), candidate.HeadCommit.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;

        var kind = EffectiveVersionRules.KindOf(state.EffectiveVersionKind);
        var sameConsumers = state.EffectiveVersionReasonCode switch
        {
            EffectiveVersionReasonCodes.Verified or EffectiveVersionReasonCodes.PackageNotPublished => hasRequiredPackages,
            EffectiveVersionReasonCodes.TokenConsumersOnly => !hasRequiredPackages,
            EffectiveVersionReasonCodes.BaselineNotOnRemoteParent or EffectiveVersionReasonCodes.ParentVersionUnavailable => true,
            _ => false,
        };
        if (!sameConsumers || kind is null or EffectiveVersionKind.Unresolved)
            return null;
        if (kind is EffectiveVersionKind.InheritedVerified or EffectiveVersionKind.InheritedUnverified && string.IsNullOrWhiteSpace(state.EffectiveVersion))
            return null;

        return new EffectiveVersionDecision(
            candidate.RepositoryId, candidate.WorkspaceRepositoryId, candidate.RepositoryName,
            kind.Value, kind == EffectiveVersionKind.Own ? candidate.GitVersion : state.EffectiveVersion!.Trim(), candidate.GitVersion,
            candidate.HeadCommit, state.EffectiveVersionReasonCode!, null);
    }

    /// <summary>
    /// The stored decision of a read only snapshot for its current HEAD, else null: its source branch version, or (only
    /// while nothing uses its version) the recorded reason that version could not be calculated. Unresolved is never
    /// reused, and neither is a decision made before snapshots had their own rule (<c>PolicyProtected</c>).
    /// </summary>
    private static EffectiveVersionDecision? StoredSnapshotDecision(Candidate candidate, StoredState state, bool hasVersionConsumer)
    {
        if (string.IsNullOrWhiteSpace(candidate.HeadCommit)
            || !string.Equals(state.EffectiveVersionCommitSha?.Trim(), candidate.HeadCommit.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;

        var kind = EffectiveVersionRules.KindOf(state.EffectiveVersionKind);
        if (kind == EffectiveVersionKind.InheritedUnverified
            && state.EffectiveVersionReasonCode == EffectiveVersionReasonCodes.ReferenceSnapshot
            && !string.IsNullOrWhiteSpace(state.EffectiveVersion))
        {
            return new EffectiveVersionDecision(
                candidate.RepositoryId, candidate.WorkspaceRepositoryId, candidate.RepositoryName,
                EffectiveVersionKind.InheritedUnverified, state.EffectiveVersion.Trim(), candidate.GitVersion, candidate.HeadCommit,
                EffectiveVersionReasonCodes.ReferenceSnapshot, null);
        }

        return kind == EffectiveVersionKind.Own
            && !hasVersionConsumer
            && state.EffectiveVersionReasonCode is EffectiveVersionReasonCodes.BaselineNotOnRemoteParent or EffectiveVersionReasonCodes.ParentVersionUnavailable
            ? Own(candidate, state.EffectiveVersionReasonCode, null)
            : null;
    }

    private sealed record StoredState(
        int WorkspaceRepositoryId,
        string? HeadCommit,
        string? GitVersion,
        string? BranchName,
        string? EffectiveVersion,
        int? EffectiveVersionKind,
        string? EffectiveVersionCommitSha,
        string? EffectiveVersionReasonCode);

    private sealed record Candidate(int RepositoryId, int WorkspaceRepositoryId, string RepositoryName, string? HeadCommit, string? GitVersion);

    private sealed record BranchPointResult(string? Version, bool? OnRemoteParent, string? Error);

    private sealed class BranchPointWorkerResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("baselineOnRemoteParent")]
        public bool? BaselineOnRemoteParent { get; set; }
    }
}
