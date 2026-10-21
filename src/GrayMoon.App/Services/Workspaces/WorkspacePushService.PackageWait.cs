using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Connectors;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Workspaces;

public sealed partial class WorkspacePushService
{
    private async Task<string> ResolveFeatureLabelAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = await _contextResolver.GetRequiredAsync(contextId, workspaceId, cancellationToken);
            return string.IsNullOrWhiteSpace(info.FeatureName) ? "Workspace" : info.FeatureName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "[PushOrchestrator] Feature label unavailable. WorkspaceId={WorkspaceId} ContextId={ContextId}", workspaceId, contextId.Value);
            return "Unknown";
        }
    }

    private async Task LogPushInvocationAsync(
        SynchronizedPushRun run,
        IReadOnlyList<PushRepoPayload> payload,
        CancellationToken cancellationToken)
    {
        var repoIds = payload.Select(repo => repo.RepoId).ToHashSet();
        var needing = await _workspaceRepository.GetRepositoryIdsNeedingPushAsync(
            run.Workspace.WorkspaceId, run.ContextId.Value, repoIds, cancellationToken);
        var links = run.Links.Where(link => repoIds.Contains(link.RepositoryId)).ToList();
        _logger.LogInformation(
            "[PushOrchestrator {RunId}] Push invocation state. WorkspaceId={WorkspaceId} Feature={Feature} Algorithm={Algorithm} NeedingPush=[{NeedingPush}] ConsideredCompleteByNeedingPushCheck=[{ConsideredComplete}] LocalCommitsPending=[{LocalCommitsPending}] NoUpstream=[{NoUpstream}] SelectedRepositoryCount={SelectedRepositoryCount}",
            run.RunId,
            run.Workspace.WorkspaceId,
            run.FeatureLabel,
            "PushEverySelectedRepository",
            FormatIds(needing.OrderBy(id => id)),
            FormatIds(repoIds.Where(id => !needing.Contains(id)).OrderBy(id => id)),
            FormatIds(links.Where(link => (link.OutgoingCommits ?? 0) > 0).Select(link => link.RepositoryId).OrderBy(id => id)),
            FormatIds(links.Where(link => link.BranchHasUpstream == false).Select(link => link.RepositoryId).OrderBy(id => id)),
            repoIds.Count);
    }

    private void LogOperationSummary(SynchronizedPushRun run, bool pushSucceeded)
    {
        var diagnostics = run.Diagnostics;
        var pushedNames = run.PushedRepos
            .Select(repo => repo.RepoName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var pushedSet = pushedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notPushed = diagnostics.RepositoryNamesByLevel
            .SelectMany(pair => pair.Value)
            .Where(name => !pushedSet.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unprocessed = diagnostics.PlannedLevels
            .Where(level => !diagnostics.CompletedLevels.Contains(level) && level != diagnostics.FailedLevel)
            .ToList();

        _logger.Log(
            pushSucceeded ? LogLevel.Information : LogLevel.Error,
            "[PushOrchestrator {RunId}] Push operation summary. WorkspaceId={WorkspaceId} Feature={Feature} PushSucceeded={PushSucceeded} CompletedLevels={CompletedLevels} FailedLevel={FailedLevel} UnprocessedLevels={UnprocessedLevels} PushedRepositories={PushedRepositories} NotPushedRepositories={NotPushedRepositories} BlockedRepositories={BlockedRepositories} BlockingPackages={BlockingPackages} FailureCategory={FailureCategory}",
            run.RunId,
            run.Workspace.WorkspaceId,
            run.FeatureLabel,
            pushSucceeded,
            diagnostics.CompletedLevels.Count == 0 ? "none" : string.Join(",", diagnostics.CompletedLevels),
            diagnostics.FailedLevel?.ToString() ?? "none",
            unprocessed.Count == 0 ? "none" : string.Join(",", unprocessed),
            pushedNames.Count == 0 ? "none" : string.Join(",", pushedNames),
            notPushed.Count == 0 ? "none" : string.Join(",", notPushed),
            diagnostics.BlockedRepositories.Count == 0 ? "none" : string.Join(",", diagnostics.BlockedRepositories),
            diagnostics.BlockingPackages.Count == 0 ? "none" : string.Join(",", diagnostics.BlockingPackages),
            pushSucceeded ? "none" : PackageWaitDiagnostics.Text(diagnostics.FailureCategory));
    }

    private async Task<Dictionary<string, PackageProducerDiagnostic>> LoadPackageProducersAsync(
        SynchronizedPushRun run,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PackageWaitDiagnostics.LoadProducersAsync(
                _dbContext,
                run.Workspace.WorkspaceId,
                run.ContextId.Value,
                run.Diagnostics.OperationRepoIds,
                run.Diagnostics.PlanLevelByRepo,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "[PushOrchestrator {RunId}] Package producer diagnostics unavailable. WorkspaceId={WorkspaceId}",
                run.RunId,
                run.Workspace.WorkspaceId);
            return new Dictionary<string, PackageProducerDiagnostic>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void LogPackageWaitStarted(
        SynchronizedPushRun run,
        int level,
        int totalLevels,
        int blockedRepositories,
        int requiredPackages,
        TimeSpan timeout,
        IReadOnlyDictionary<int, Connector?> connectors)
    {
        var names = connectors.Values
            .Select(connector => connector?.ConnectorName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _logger.LogInformation(
            "[PushOrchestrator {RunId}] Package readiness wait started. WorkspaceId={WorkspaceId} Feature={Feature} Level={Level} TotalLevels={TotalLevels} RequiredPackages={RequiredPackages} BlockedRepositories={BlockedRepositories} TimeoutSeconds={TimeoutSeconds} PollIntervalSeconds={PollIntervalSeconds} ConnectorIds={ConnectorIds} RegistryNames={RegistryNames}",
            run.RunId,
            run.Workspace.WorkspaceId,
            run.FeatureLabel,
            level,
            totalLevels,
            requiredPackages,
            blockedRepositories,
            (int)timeout.TotalSeconds,
            PackageWaitDiagnostics.PollIntervalSeconds,
            connectors.Count == 0 ? "none" : string.Join(",", connectors.Keys.OrderBy(id => id)),
            names.Count == 0 ? "Unknown" : string.Join(",", names));
    }

    private void LogPackageExpectations(
        string? runId,
        int level,
        IReadOnlyList<RequiredPackageForPush> required,
        IReadOnlyDictionary<string, PackageProducerDiagnostic> producers)
    {
        foreach (var package in required)
        {
            if (!producers.TryGetValue(PackageWaitDiagnostics.Key(package.PackageId, package.Version), out var producer))
            {
                _logger.LogDebug(
                    "[PushOrchestrator {RunId}] Package producer not resolved. Level={Level} PackageId={PackageId} ExpectedVersion={ExpectedVersion} ExpectedVersionSource={ExpectedVersionSource}",
                    runId, level, package.PackageId, package.Version, PackageWaitDiagnostics.ExpectedVersionSource);
                continue;
            }

            if (producer.Expectation != PackageExpectationKind.VersionMismatchSuspected)
                continue;

            _logger.LogWarning(
                "[PushOrchestrator {RunId}] Expected package version differs from known version metadata. Level={Level} PackageId={PackageId} ExpectedVersion={ExpectedVersion} ExpectedVersionSource={ExpectedVersionSource} KnownVersion={KnownVersion} KnownVersionSource={KnownVersionSource} ProducerRepository={ProducerRepository}",
                runId,
                level,
                package.PackageId,
                package.Version,
                PackageWaitDiagnostics.ExpectedVersionSource,
                PackageWaitDiagnostics.Text(producer.KnownVersion),
                producer.KnownVersionSource,
                producer.ProducerRepository);
        }
    }

    private async Task LogPackageWaitTimeoutAsync(
        SynchronizedPushRun run,
        int level,
        int totalLevels,
        IReadOnlyList<PushRepoPayload> reposAtLevel,
        IReadOnlyList<RequiredPackageForPush> required,
        PackageWaitObservation[] observations,
        IReadOnlyDictionary<string, PackageProducerDiagnostic> producers,
        IReadOnlyDictionary<int, Connector?> connectors,
        bool[] foundByIndex,
        DateTime waitStartedUtc,
        CancellationToken cancellationToken)
    {
        var missing = new List<int>();
        for (var index = 0; index < required.Count; index++)
        {
            if (!foundByIndex[index])
                missing.Add(index);
        }

        var elapsedSeconds = (int)Math.Round((DateTime.UtcNow - waitStartedUtc).TotalSeconds);
        var available = required.Count - missing.Count;
        _logger.LogError(
            "[PushOrchestrator {RunId}] Push dependency wait timed out. WorkspaceId={WorkspaceId} Feature={Feature} Level={Level} TotalLevels={TotalLevels} ElapsedSeconds={ElapsedSeconds} RequiredPackages={RequiredPackages} AvailablePackages={AvailablePackages} MissingPackages={MissingPackages}",
            run.RunId,
            run.Workspace.WorkspaceId,
            run.FeatureLabel,
            level,
            totalLevels,
            elapsedSeconds,
            required.Count,
            available,
            missing.Count);

        var pushedIds = run.PushedRepos.Select(repo => repo.RepoId).ToHashSet();
        var workflowByProducer = new Dictionary<int, PackageWorkflowDiagnostic>();
        var workflowLookups = 0;
        foreach (var index in missing)
        {
            var package = required[index];
            var observation = observations[index];
            producers.TryGetValue(PackageWaitDiagnostics.Key(package.PackageId, package.Version), out var producer);
            connectors.TryGetValue(package.MatchedConnectorId ?? -1, out var connector);
            var consumers = reposAtLevel
                .Where(repo => repo.RequiredPackages.Any(requiredPackage =>
                    string.Equals(requiredPackage.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(requiredPackage.Version, package.Version, StringComparison.OrdinalIgnoreCase)
                    && requiredPackage.MatchedConnectorId == package.MatchedConnectorId))
                .Select(repo => repo.RepoName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var blocking = package.PackageId + "@" + package.Version;
            if (!run.Diagnostics.BlockingPackages.Contains(blocking, StringComparer.OrdinalIgnoreCase))
                run.Diagnostics.BlockingPackages.Add(blocking);

            var batchNumber = 0;
            foreach (var batch in PackageWaitDiagnostics.ConsumerBatches(consumers))
            {
                batchNumber++;
                _logger.LogError(
                    "[PushOrchestrator {RunId}] Required package unavailable. PackageId={PackageId} ExpectedVersion={ExpectedVersion} ExpectedVersionSource={ExpectedVersionSource} ConnectorId={ConnectorId} RegistryName={RegistryName} RegistryType={RegistryType} DependencyLevel={DependencyLevel} ProducerRepository={ProducerRepository} ProducerBranch={ProducerBranch} ProducerProject={ProducerProject} ProducerLevel={ProducerLevel} ProducerStatus={ProducerStatus} Expectation={Expectation} ConsumerRepositories={ConsumerRepositories} ConsumerCount={ConsumerCount} ConsumerBatch={ConsumerBatch} KnownVersion={KnownVersion} KnownVersionSource={KnownVersionSource} LastLookupResult={LastLookupResult} LastSuccessfulCheck={LastSuccessfulCheck} ElapsedSeconds={ElapsedSeconds} AttemptCount={AttemptCount}",
                    run.RunId,
                    package.PackageId,
                    package.Version,
                    PackageWaitDiagnostics.ExpectedVersionSource,
                    connector?.ConnectorId.ToString() ?? package.MatchedConnectorId?.ToString() ?? "Unknown",
                    PackageWaitDiagnostics.Text(connector?.ConnectorName),
                    PackageWaitDiagnostics.RegistryType(connector),
                    level,
                    producer?.ProducerRepository ?? "Unknown",
                    producer?.ProducerBranch ?? "Unknown",
                    producer?.ProducerProject ?? "Unknown",
                    producer?.ProducerLevel.ToString() ?? "Unknown",
                    ProducerStatus(producer, pushedIds),
                    producer?.Expectation.ToString() ?? nameof(PackageExpectationKind.ProducerNotResolved),
                    batch,
                    consumers.Count,
                    batchNumber,
                    PackageWaitDiagnostics.Text(producer?.KnownVersion),
                    producer?.KnownVersionSource ?? "Unknown",
                    observation.LastKind?.ToString() ?? "Unknown",
                    observation.LastSuccessfulCheckUtc?.ToString("o") ?? "Unknown",
                    elapsedSeconds,
                    observation.AttemptCount);
            }

            var workflow = await WorkflowForAsync(run, producer, workflowByProducer, () => workflowLookups, value => workflowLookups = value, cancellationToken);
            LogPublication(run, package, producer, pushedIds, workflow);
        }
    }

    private async Task<PackageWorkflowDiagnostic> WorkflowForAsync(
        SynchronizedPushRun run,
        PackageProducerDiagnostic? producer,
        Dictionary<int, PackageWorkflowDiagnostic> workflowByProducer,
        Func<int> readLookups,
        Action<int> writeLookups,
        CancellationToken cancellationToken)
    {
        if (producer == null)
        {
            return new PackageWorkflowDiagnostic(
                PackageWaitDiagnostics.WorkflowCorrelationUnknown, null, null, null, null, null,
                PackageWaitDiagnostics.PublicationUnverified, "Unknown", "Unknown", false);
        }

        if (workflowByProducer.TryGetValue(producer.ProducerRepositoryId, out var cached))
            return cached;

        PackageWorkflowDiagnostic diagnostic;
        var canQuery = producer.GitHubConnectorId != null
            && !string.IsNullOrWhiteSpace(producer.HeadCommit)
            && !string.IsNullOrWhiteSpace(producer.Owner)
            && !string.IsNullOrWhiteSpace(producer.RepositoryName)
            && producer.ProducerBranch != "Unknown";
        if (!canQuery)
        {
            diagnostic = PackageWaitDiagnostics.Correlate(
                producer.HeadCommit, PackageWaitDiagnostics.HeadCommitSource, null, lookupAttempted: false, lookupFailed: false);
        }
        else if (readLookups() >= PackageWaitDiagnostics.WorkflowLookupBudget)
        {
            diagnostic = new PackageWorkflowDiagnostic(
                PackageWaitDiagnostics.WorkflowStatusUnavailable, null, null, null, null, null,
                PackageWaitDiagnostics.PublicationUnverified,
                PackageWaitDiagnostics.Text(producer.HeadCommit),
                PackageWaitDiagnostics.HeadCommitSource,
                false);
            _logger.LogDebug(
                "[PushOrchestrator {RunId}] Package publication lookup budget reached. ProducerRepository={ProducerRepository}",
                run.RunId,
                producer.ProducerRepository);
        }
        else
        {
            writeLookups(readLookups() + 1);
            diagnostic = await LookupProducerWorkflowAsync(producer, cancellationToken);
        }

        workflowByProducer[producer.ProducerRepositoryId] = diagnostic;
        return diagnostic;
    }

    private async Task<PackageWorkflowDiagnostic> LookupProducerWorkflowAsync(
        PackageProducerDiagnostic producer,
        CancellationToken cancellationToken)
    {
        if (producer.GitHubConnectorId is not int connectorId
            || string.IsNullOrWhiteSpace(producer.HeadCommit)
            || string.IsNullOrWhiteSpace(producer.Owner)
            || string.IsNullOrWhiteSpace(producer.RepositoryName)
            || producer.ProducerBranch == "Unknown")
        {
            return PackageWaitDiagnostics.Correlate(
                producer.HeadCommit,
                PackageWaitDiagnostics.HeadCommitSource,
                null,
                lookupAttempted: false,
                lookupFailed: false);
        }

        return await PackageWaitDiagnostics.ReadWorkflowAsync(
            async token =>
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var connector = await scope.ServiceProvider.GetRequiredService<ConnectorRepository>().GetByIdAsync(connectorId);
                if (connector == null || connector.ConnectorType != ConnectorType.GitHub)
                    throw new InvalidOperationException("GitHub connector is not available for this repository.");

                var runs = await scope.ServiceProvider.GetRequiredService<GitHubService>().GetWorkflowRunsForBranchAsync(
                    connector,
                    producer.Owner!,
                    producer.RepositoryName!,
                    producer.ProducerBranch,
                    perPage: 20,
                    cancellationToken: token);
                return runs;
            },
            producer.HeadCommit,
            PackageWaitDiagnostics.HeadCommitSource,
            PackageWaitDiagnostics.WorkflowLookupBound,
            cancellationToken);
    }

    private void LogPublication(
        SynchronizedPushRun run,
        RequiredPackageForPush package,
        PackageProducerDiagnostic? producer,
        IReadOnlySet<int> pushedIds,
        PackageWorkflowDiagnostic workflow)
    {
        PackageWaitDiagnostics.LogPublication(
            _logger,
            run.RunId,
            package.PackageId,
            package.Version,
            producer?.ProducerRepository,
            ProducerStatus(producer, pushedIds),
            workflow);
    }

    private static string ProducerStatus(PackageProducerDiagnostic? producer, IReadOnlySet<int> pushedIds)
    {
        if (producer == null)
            return "Unknown";
        if (!producer.InCurrentOperation)
            return "NotInThisOperation";
        return pushedIds.Contains(producer.ProducerRepositoryId) ? "Succeeded" : "NotPushed";
    }

    private static string FormatIds(IEnumerable<int> ids)
    {
        var text = string.Join(",", ids);
        return text.Length == 0 ? "none" : text;
    }
}
