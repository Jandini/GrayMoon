using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Connectors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>
/// Observes a synchronized push. Nothing here decides whether a package is ready, how long to wait,
/// or which repository is pushed.
/// </summary>
internal static class PackageWaitDiagnostics
{
    internal const int PollIntervalSeconds = 2;
    internal static readonly TimeSpan WorkflowLookupBound = TimeSpan.FromSeconds(3);
    internal const int ConsumerNameBatchSize = 12;
    internal const int WorkflowLookupBudget = 4;

    internal const string FailurePackageWaitTimeout = "PackageWaitTimeout";
    internal const string FailurePackageWaitFailed = "PackageWaitFailed";
    internal const string FailureRepositoryPushFailed = "RepositoryPushFailed";
    internal const string FailureRestoreFailed = "RestoreFailed";

    internal const string WorkflowRunning = "WorkflowRunning";
    internal const string WorkflowSucceeded = "WorkflowSucceeded";
    internal const string WorkflowFailed = "WorkflowFailed";
    internal const string WorkflowCancelled = "WorkflowCancelled";
    internal const string WorkflowNotFound = "WorkflowNotFound";
    internal const string WorkflowCorrelationUnknown = "WorkflowCorrelationUnknown";
    internal const string WorkflowStatusUnavailable = "WorkflowStatusUnavailable";

    internal const string PublicationUnverified = "Unverified";
    internal const string ExpectedVersionSource = "ProjectDependency.Version";
    internal const string HeadCommitSource = "WorkspaceRepositoryContextState.HeadCommit";

    internal static string Text(string? value)
        => string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();

    internal static string RegistryType(Connector? connector)
    {
        if (connector == null || string.IsNullOrWhiteSpace(connector.ApiBaseUrl))
            return "Unknown";
        if (ConnectorHelpers.IsGitHubPackages(connector.ApiBaseUrl))
            return "GitHubPackages";
        if (ConnectorHelpers.IsNuGetOrg(connector.ApiBaseUrl))
            return "NuGetOrg";
        if (ConnectorHelpers.IsProGet(connector.ApiBaseUrl))
            return "ProGet";
        return connector.ConnectorType.ToString();
    }

    internal static LogLevel LookupLevel(PackageVersionLookupKind kind)
        => kind switch
        {
            PackageVersionLookupKind.Available or PackageVersionLookupKind.VersionNotFound or PackageVersionLookupKind.Canceled
                => LogLevel.Debug,
            _ => LogLevel.Warning
        };

    internal static void LogLookupTransition(
        ILogger logger,
        string? runId,
        string packageId,
        string version,
        int? connectorId,
        string? registryName,
        PackageVersionLookupKind kind,
        int? statusCode)
    {
        logger.Log(
            LookupLevel(kind),
            "[PushOrchestrator {RunId}] Package lookup changed. PackageId={PackageId} ExpectedVersion={ExpectedVersion} ConnectorId={ConnectorId} RegistryName={RegistryName} LastLookupResult={LastLookupResult} StatusCode={StatusCode}",
            runId,
            packageId,
            version,
            connectorId,
            Text(registryName),
            kind,
            statusCode?.ToString() ?? "Unknown");
    }

    internal static void LogLevelOutcome(
        ILogger logger,
        string? runId,
        int workspaceId,
        int level,
        int totalLevels,
        int processed,
        int pushed,
        int failed,
        int skipped,
        int blocked,
        int requiredPackages,
        double elapsedSeconds,
        bool succeeded,
        string? failureCategory)
    {
        if (succeeded)
        {
            logger.LogInformation(
                "[PushOrchestrator {RunId}] Workspace {WorkspaceId}: Level {Level}: completed. Pushed {PushedCount} repo(s). TotalLevels={TotalLevels} Processed={Processed} Failed={Failed} Skipped={Skipped} Blocked={Blocked} RequiredPackages={RequiredPackages} ElapsedSeconds={ElapsedSeconds}",
                runId, workspaceId, level, pushed, totalLevels, processed, failed, skipped, blocked, requiredPackages, elapsedSeconds);
            return;
        }

        logger.LogInformation(
            "[PushOrchestrator {RunId}] Workspace {WorkspaceId}: Level {Level}: stopped. TotalLevels={TotalLevels} Processed={Processed} Pushed={Pushed} Failed={Failed} Skipped={Skipped} Blocked={Blocked} RequiredPackages={RequiredPackages} ElapsedSeconds={ElapsedSeconds} FailureCategory={FailureCategory}",
            runId, workspaceId, level, totalLevels, processed, pushed, failed, skipped, blocked, requiredPackages, elapsedSeconds, Text(failureCategory));
    }

    /// <summary>
    /// One read of the dependency edges already used to build the push plan. Versions are compared and reported;
    /// the version the wait checks is still the dependency edge.
    /// </summary>
    internal static async Task<Dictionary<string, PackageProducerDiagnostic>> LoadProducersAsync(
        AppDbContext db,
        int workspaceId,
        int workspaceFeatureContextId,
        IReadOnlySet<int> operationRepoIds,
        IReadOnlyDictionary<int, int?> planLevelByRepo,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, PackageProducerDiagnostic>(StringComparer.OrdinalIgnoreCase);

        var isSpecialWorkspace = await db.WorkspaceFeatureContexts
            .AsNoTracking()
            .Where(c => c.WorkspaceFeatureContextId == workspaceFeatureContextId)
            .Select(c => c.Kind == WorkspaceFeatureContextKind.Workspace)
            .FirstOrDefaultAsync(cancellationToken);

        var links = await db.WorkspaceRepositories
            .AsNoTracking()
            .Include(link => link.Repository)
            .ThenInclude(repository => repository!.Connector)
            .Where(link => link.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
            return result;

        var linkIds = links.Select(link => link.WorkspaceRepositoryId).ToList();
        var states = await db.WorkspaceRepositoryContextStates
            .AsNoTracking()
            .Where(state => state.WorkspaceFeatureContextId == workspaceFeatureContextId && linkIds.Contains(state.WorkspaceRepositoryId))
            .ToListAsync(cancellationToken);
        var stateByLinkId = states.ToDictionary(state => state.WorkspaceRepositoryId);

        var versionByRepo = new Dictionary<int, (string? Version, string Source)>();
        var levelByRepo = new Dictionary<int, int?>();
        var branchByRepo = new Dictionary<int, string?>();
        var headByRepo = new Dictionary<int, string?>();
        foreach (var link in links)
        {
            if (stateByLinkId.TryGetValue(link.WorkspaceRepositoryId, out var state))
            {
                // A commit-count write can create a context row that has no GitVersion yet. The link still holds
                // the version the plan compared. A probed version on the row wins when one is present.
                if (!string.IsNullOrWhiteSpace(state.GitVersion))
                    versionByRepo[link.RepositoryId] = (state.GitVersion, "WorkspaceRepositoryContextState.GitVersion");
                else if (isSpecialWorkspace)
                    versionByRepo[link.RepositoryId] = (link.GitVersion, "WorkspaceRepositoryLink.GitVersion");
                else
                    versionByRepo[link.RepositoryId] = (null, "Unknown");
                levelByRepo[link.RepositoryId] = state.DependencyLevel ?? (isSpecialWorkspace ? link.DependencyLevel : null);
                branchByRepo[link.RepositoryId] = string.IsNullOrWhiteSpace(state.BranchName) ? link.BranchName : state.BranchName;
                headByRepo[link.RepositoryId] = state.HeadCommit;
            }
            else if (isSpecialWorkspace)
            {
                versionByRepo[link.RepositoryId] = (link.GitVersion, "WorkspaceRepositoryLink.GitVersion");
                levelByRepo[link.RepositoryId] = link.DependencyLevel;
                branchByRepo[link.RepositoryId] = link.BranchName;
                headByRepo[link.RepositoryId] = null;
            }
            else
            {
                versionByRepo[link.RepositoryId] = (null, "Unknown");
                levelByRepo[link.RepositoryId] = null;
                branchByRepo[link.RepositoryId] = null;
                headByRepo[link.RepositoryId] = null;
            }
        }

        var sourceLevels = links
            .Where(link => link.Role != WorkspaceRepositoryRole.Workspace && levelByRepo.GetValueOrDefault(link.RepositoryId).HasValue)
            .Select(link => levelByRepo[link.RepositoryId]!.Value)
            .ToList();
        var workspaceRepoLevel = (sourceLevels.Count > 0 ? sourceLevels.Max() : 0) + 1;
        foreach (var link in links.Where(link => link.Role == WorkspaceRepositoryRole.Workspace))
            levelByRepo[link.RepositoryId] = workspaceRepoLevel;

        var repoIds = links.Select(link => link.RepositoryId).ToHashSet();
        // The plan's level is the one this push is walking. A dependency recompute after an earlier level can
        // write a different level onto the context row; that must not change which producer this wait reports.
        int? LevelFor(int repoId)
        {
            if (planLevelByRepo.TryGetValue(repoId, out var planLevel) && planLevel.HasValue)
                return planLevel;
            return levelByRepo.GetValueOrDefault(repoId);
        }

        var maxLevel = repoIds.Select(LevelFor).Where(level => level.HasValue).Select(level => level!.Value).DefaultIfEmpty(0).Max();
        int EffectiveLevel(int repoId) => LevelFor(repoId) ?? maxLevel + 1;

        var projects = await db.WorkspaceProjects
            .AsNoTracking()
            .Where(project => project.WorkspaceId == workspaceId
                && repoIds.Contains(project.RepositoryId)
                && (project.WorkspaceFeatureContextId == workspaceFeatureContextId || project.IsGenerated))
            .Select(project => new
            {
                project.ProjectId,
                project.RepositoryId,
                project.PackageId,
                project.ProjectName,
                project.ProjectFilePath
            })
            .ToListAsync(cancellationToken);
        var byProject = projects.ToDictionary(project => project.ProjectId);
        var projectIds = byProject.Keys.ToHashSet();
        if (projectIds.Count == 0)
            return result;

        var dependencies = await db.ProjectDependencies
            .AsNoTracking()
            .Where(dependency => projectIds.Contains(dependency.DependentProjectId) && projectIds.Contains(dependency.ReferencedProjectId))
            .Select(dependency => new { dependency.DependentProjectId, dependency.ReferencedProjectId, dependency.Version })
            .ToListAsync(cancellationToken);

        var linkByRepo = links.ToDictionary(link => link.RepositoryId);
        foreach (var dependency in dependencies)
        {
            if (!byProject.TryGetValue(dependency.DependentProjectId, out var dependent)
                || !byProject.TryGetValue(dependency.ReferencedProjectId, out var producer))
                continue;
            if (EffectiveLevel(producer.RepositoryId) >= EffectiveLevel(dependent.RepositoryId))
                continue;

            var packageId = (!string.IsNullOrWhiteSpace(producer.PackageId) ? producer.PackageId : producer.ProjectName)?.Trim() ?? "";
            var version = dependency.Version?.Trim() ?? "";
            if (string.IsNullOrEmpty(packageId))
                continue;

            var key = Key(packageId, version);
            var (knownVersion, knownSource) = versionByRepo.GetValueOrDefault(producer.RepositoryId);
            var inOperation = operationRepoIds.Contains(producer.RepositoryId);
            var mismatch = !string.IsNullOrWhiteSpace(knownVersion)
                && !string.Equals(knownVersion.Trim(), version, StringComparison.OrdinalIgnoreCase);
            var expectation = mismatch
                ? PackageExpectationKind.VersionMismatchSuspected
                : inOperation
                    ? PackageExpectationKind.CurrentOperationProducer
                    : PackageExpectationKind.ExternalOrPreviouslyPublished;

            linkByRepo.TryGetValue(producer.RepositoryId, out var producerLink);
            var gitHubConnectorId = producerLink?.Repository?.Connector?.ConnectorType == ConnectorType.GitHub
                ? producerLink.Repository.ConnectorId
                : (int?)null;

            var diagnostic = new PackageProducerDiagnostic(
                packageId,
                version,
                producer.RepositoryId,
                Text(producerLink?.Repository?.RepositoryName),
                Text(branchByRepo.GetValueOrDefault(producer.RepositoryId)),
                Text(producer.ProjectFilePath),
                EffectiveLevel(producer.RepositoryId),
                string.IsNullOrWhiteSpace(knownVersion) ? null : knownVersion.Trim(),
                string.IsNullOrWhiteSpace(knownVersion) ? "Unknown" : knownSource,
                string.IsNullOrWhiteSpace(headByRepo.GetValueOrDefault(producer.RepositoryId))
                    ? null
                    : headByRepo[producer.RepositoryId]!.Trim(),
                gitHubConnectorId,
                producerLink?.Repository?.OrgName,
                producerLink?.Repository?.RepositoryName,
                inOperation,
                expectation);

            if (!result.TryGetValue(key, out var existing) || diagnostic.ProducerLevel < existing.ProducerLevel)
                result[key] = diagnostic;
        }

        return result;
    }

    internal static string Key(string packageId, string version)
        => packageId.Trim().ToLowerInvariant() + "\n" + version.Trim();

    internal static string MapWorkflowStatus(string? status, string? conclusion)
    {
        if (IsRunning(status))
            return WorkflowRunning;

        if (string.Equals(conclusion, "success", StringComparison.OrdinalIgnoreCase))
            return WorkflowSucceeded;
        if (string.Equals(conclusion, "failure", StringComparison.OrdinalIgnoreCase)
            || string.Equals(conclusion, "timed_out", StringComparison.OrdinalIgnoreCase)
            || string.Equals(conclusion, "startup_failure", StringComparison.OrdinalIgnoreCase)
            || string.Equals(conclusion, "action_required", StringComparison.OrdinalIgnoreCase))
            return WorkflowFailed;
        if (string.Equals(conclusion, "cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(conclusion, "canceled", StringComparison.OrdinalIgnoreCase))
            return WorkflowCancelled;
        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            return WorkflowStatusUnavailable;
        return WorkflowCorrelationUnknown;
    }

    internal static PackageWorkflowDiagnostic Correlate(
        string? commitSha,
        string? commitShaSource,
        IReadOnlyList<GitHubWorkflowRunDto>? runs,
        bool lookupAttempted,
        bool lookupFailed)
    {
        var sha = Text(commitSha);
        var source = Text(commitShaSource);
        if (string.IsNullOrWhiteSpace(commitSha))
        {
            return new PackageWorkflowDiagnostic(
                WorkflowCorrelationUnknown, null, null, null, null, null, PublicationUnverified, "Unknown", "Unknown", false);
        }

        if (lookupFailed || !lookupAttempted)
        {
            return new PackageWorkflowDiagnostic(
                WorkflowStatusUnavailable, null, null, null, null, null, PublicationUnverified, sha, source, false);
        }

        var matches = (runs ?? [])
            .Where(run => !string.IsNullOrWhiteSpace(run.HeadSha)
                && string.Equals(run.HeadSha.Trim(), commitSha.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(run => run.UpdatedAt ?? DateTimeOffset.MinValue)
            .ToList();
        if (matches.Count == 0)
        {
            return new PackageWorkflowDiagnostic(
                WorkflowNotFound, null, null, null, null, null, PublicationUnverified, sha, source, false);
        }

        var match = matches[0];
        return new PackageWorkflowDiagnostic(
            MapWorkflowStatus(match.Status, match.Conclusion),
            string.IsNullOrWhiteSpace(match.Name) ? null : match.Name,
            match.Id > 0 ? match.Id : null,
            string.IsNullOrWhiteSpace(match.HtmlUrl) ? null : match.HtmlUrl,
            match.Conclusion,
            match.UpdatedAt,
            PublicationUnverified,
            sha,
            source,
            true);
    }

    /// <summary>
    /// Runs one caller-supplied lookup, bounded by <paramref name="bound"/> and <paramref name="cancellationToken"/>.
    /// A lookup failure, timeout, or cancellation becomes a diagnostic status and does not escape.
    /// </summary>
    internal static async Task<PackageWorkflowDiagnostic> ReadWorkflowAsync(
        Func<CancellationToken, Task<IReadOnlyList<GitHubWorkflowRunDto>>>? lookup,
        string? commitSha,
        string? commitShaSource,
        TimeSpan bound,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(commitSha) || lookup == null)
            return Correlate(commitSha, commitShaSource, null, lookupAttempted: false, lookupFailed: false);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (bound > TimeSpan.Zero)
            bounded.CancelAfter(bound);

        try
        {
            var runs = await lookup(bounded.Token).ConfigureAwait(false);
            return Correlate(commitSha, commitShaSource, runs, lookupAttempted: true, lookupFailed: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Correlate(commitSha, commitShaSource, null, lookupAttempted: true, lookupFailed: true);
        }
        catch (OperationCanceledException)
        {
            return new PackageWorkflowDiagnostic(
                WorkflowStatusUnavailable, null, null, null, null, null, PublicationUnverified, Text(commitSha), Text(commitShaSource), false);
        }
        catch (Exception)
        {
            return Correlate(commitSha, commitShaSource, null, lookupAttempted: true, lookupFailed: true);
        }
    }

    internal static IEnumerable<string> ConsumerBatches(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            yield return "Unknown";
            yield break;
        }

        for (var offset = 0; offset < names.Count; offset += ConsumerNameBatchSize)
        {
            var count = Math.Min(ConsumerNameBatchSize, names.Count - offset);
            yield return string.Join(",", names.Skip(offset).Take(count));
        }
    }

    internal static void LogPublication(
        ILogger logger,
        string? runId,
        string packageId,
        string expectedVersion,
        string? producerRepository,
        string producerPushStatus,
        PackageWorkflowDiagnostic workflow)
    {
        var level = producerPushStatus == "Succeeded" || workflow.WorkflowStatus == WorkflowFailed
            ? LogLevel.Warning
            : LogLevel.Debug;
        logger.Log(
            level,
            "[PushOrchestrator {RunId}] Package publication diagnostics. PackageId={PackageId} ExpectedVersion={ExpectedVersion} ProducerRepository={ProducerRepository} ProducerPushStatus={ProducerPushStatus} CommitSha={CommitSha} CommitShaSource={CommitShaSource} WorkflowStatus={WorkflowStatus} WorkflowName={WorkflowName} WorkflowRunId={WorkflowRunId} WorkflowRunUrl={WorkflowRunUrl} WorkflowConclusion={WorkflowConclusion} WorkflowUpdatedAt={WorkflowUpdatedAt} PublicationStatus={PublicationStatus} WorkflowCorrelatedToCommit={WorkflowCorrelatedToCommit}",
            runId,
            packageId,
            expectedVersion,
            Text(producerRepository),
            producerPushStatus,
            workflow.CommitSha,
            workflow.CommitShaSource,
            workflow.WorkflowStatus,
            Text(workflow.WorkflowName),
            workflow.WorkflowRunId?.ToString() ?? "Unknown",
            Text(workflow.WorkflowRunUrl),
            Text(workflow.WorkflowConclusion),
            workflow.WorkflowUpdatedAt?.ToString("o") ?? "Unknown",
            workflow.PublicationStatus,
            workflow.CorrelatedToCommit);
    }

    private static bool IsRunning(string? status)
        => string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "waiting", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "requested", StringComparison.OrdinalIgnoreCase);
}

internal enum PackageExpectationKind
{
    CurrentOperationProducer,
    ExternalOrPreviouslyPublished,
    ProducerNotResolved,
    VersionMismatchSuspected,
    Unknown
}

internal sealed record PackageProducerDiagnostic(
    string PackageId,
    string ExpectedVersion,
    int ProducerRepositoryId,
    string ProducerRepository,
    string ProducerBranch,
    string ProducerProject,
    int ProducerLevel,
    string? KnownVersion,
    string KnownVersionSource,
    string? HeadCommit,
    int? GitHubConnectorId,
    string? Owner,
    string? RepositoryName,
    bool InCurrentOperation,
    PackageExpectationKind Expectation);

internal sealed record PackageWorkflowDiagnostic(
    string WorkflowStatus,
    string? WorkflowName,
    long? WorkflowRunId,
    string? WorkflowRunUrl,
    string? WorkflowConclusion,
    DateTimeOffset? WorkflowUpdatedAt,
    string PublicationStatus,
    string CommitSha,
    string CommitShaSource,
    bool CorrelatedToCommit);

/// <summary>Counts for one required package across the polls of a single wait. Not a second source of push state.</summary>
internal sealed class PackageWaitObservation
{
    public PackageVersionLookupKind? LastKind { get; private set; }
    public int? LastStatusCode { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime? LastSuccessfulCheckUtc { get; private set; }

    public bool Observe(PackageVersionLookupKind kind, int? statusCode, bool successfulQuery, DateTime utcNow)
    {
        AttemptCount++;
        if (successfulQuery)
            LastSuccessfulCheckUtc = utcNow;
        var changed = LastKind != kind;
        LastKind = kind;
        LastStatusCode = statusCode;
        return changed;
    }
}

/// <summary>Level and package facts already decided by the push, collected so the operation summary can name them.</summary>
internal sealed class PushRunDiagnostics
{
    public HashSet<int> OperationRepoIds { get; } = [];
    public List<int> PlannedLevels { get; } = [];
    public Dictionary<int, List<string>> RepositoryNamesByLevel { get; } = [];
    public List<int> CompletedLevels { get; } = [];
    public int? FailedLevel { get; set; }
    public string? FailureCategory { get; set; }
    public List<string> BlockingPackages { get; } = [];
    public List<string> BlockedRepositories { get; } = [];

    /// <summary>Dependency level from the push plan this run is executing. Later context-state writes must not replace it.</summary>
    public Dictionary<int, int?> PlanLevelByRepo { get; } = [];

    public void RememberPlan(IEnumerable<PushRepoPayload> payload)
    {
        if (PlannedLevels.Count > 0)
            return;

        var repos = payload as IReadOnlyList<PushRepoPayload> ?? payload.ToList();
        foreach (var repo in repos)
        {
            OperationRepoIds.Add(repo.RepoId);
            PlanLevelByRepo[repo.RepoId] = repo.DependencyLevel;
        }

        foreach (var group in repos.GroupBy(repo => repo.DependencyLevel ?? 0).OrderBy(group => group.Key))
        {
            PlannedLevels.Add(group.Key);
            RepositoryNamesByLevel[group.Key] = group.Select(repo => repo.RepoName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
