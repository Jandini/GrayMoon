using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Ui;

namespace GrayMoon.App.Services.Ci;

/// <summary>
/// GitHub Actions run watching during synchronized push: every few seconds discovers running workflows on the
/// branches already pushed, then streams their job/step transitions into the push overlay terminal. Moved out of
/// <see cref="WorkspacePushService"/> unchanged; one instance per dependency level, like the state it replaced.
/// </summary>
internal sealed class GitHubActionsPushRunWatch(
    GitHubActionsService gitHubActionsService,
    GhaWorkflowLiveFeedService liveFeedService,
    OverlayCommandTerminalService overlayTerminal,
    ILogger logger) : IPushCiRunWatch
{
    private const int DiscoveryIntervalSeconds = 6;

    private readonly Dictionary<string, GhaWorkflowLiveFeedState> _feedByRunKey = new(StringComparer.Ordinal);
    private readonly HashSet<int> _noWorkflowRepoIds = [];
    private bool _discoveryEnabled = true;
    private DateTime _lastDiscoveryUtc = DateTime.MinValue;
    private DateTime _lastPollUtc = DateTime.MinValue;

    public async Task TickAsync(
        IReadOnlyList<PushRepoPayload> pushedRepos,
        IReadOnlyList<WorkspaceRepositoryLink> links,
        CancellationToken cancellationToken)
    {
        if (!_discoveryEnabled)
            return;

        if ((DateTime.UtcNow - _lastDiscoveryUtc).TotalSeconds >= DiscoveryIntervalSeconds)
        {
            _lastDiscoveryUtc = DateTime.UtcNow;
            await DiscoverRunningWorkflowsAsync(pushedRepos, links, cancellationToken);

            // Permanently disable only when every previously-pushed repo is confirmed to have no workflows at all.
            if (pushedRepos.Count > 0 && pushedRepos.All(r => _noWorkflowRepoIds.Contains(r.RepoId)))
                _discoveryEnabled = false;
        }

        if (_feedByRunKey.Count > 0 && (DateTime.UtcNow - _lastPollUtc).TotalMilliseconds >= GhaWorkflowLiveFeedService.PollIntervalActiveMs)
        {
            _lastPollUtc = DateTime.UtcNow;
            await PumpLiveFeedIntoOverlayAsync(cancellationToken);
        }
    }

    private async Task DiscoverRunningWorkflowsAsync(
        IReadOnlyList<PushRepoPayload> pushedRepos,
        IReadOnlyList<WorkspaceRepositoryLink> links,
        CancellationToken cancellationToken)
    {
        var linksByRepoId = links.ToDictionary(l => l.RepositoryId);
        foreach (var repo in pushedRepos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_noWorkflowRepoIds.Contains(repo.RepoId))
                continue;

            if (!linksByRepoId.TryGetValue(repo.RepoId, out var link)
                || link.Repository?.Connector == null
                || string.IsNullOrWhiteSpace(link.BranchName)
                || string.IsNullOrWhiteSpace(link.Repository.OrgName)
                || string.IsNullOrWhiteSpace(link.Repository.RepositoryName))
            {
                continue;
            }

            var entry = new GitHubRepositoryEntry
            {
                RepositoryId = link.Repository.RepositoryId,
                ConnectorName = link.Repository.Connector.ConnectorName,
                OrgName = link.Repository.OrgName,
                RepositoryName = link.Repository.RepositoryName,
                CloneUrl = link.Repository.CloneUrl,
                Visibility = link.Repository.Visibility,
                Archived = link.Repository.Archived
            };

            IReadOnlyList<ActionStatusInfo>? statuses;
            try
            {
                statuses = await gitHubActionsService.GetWorkflowStatusesForBranchAsync(entry, link.BranchName!, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Push wait: failed to discover running workflows for repo {RepoId}", repo.RepoId);
                continue;
            }

            if (statuses == null || statuses.Count == 0)
            {
                _noWorkflowRepoIds.Add(repo.RepoId);
                logger.LogDebug("Push wait: repo {RepoName} has no active GitHub Actions workflows; skipping live feed.", entry.RepositoryName);
                continue;
            }

            foreach (var status in statuses)
            {
                if (!string.Equals(status.Status, "running", StringComparison.OrdinalIgnoreCase)
                    || !status.RunId.HasValue
                    || status.RunId.Value <= 0)
                {
                    continue;
                }

                var runKey = $"{entry.ConnectorName}|{entry.OrgName}|{entry.RepositoryName}|{status.RunId.Value}";
                if (_feedByRunKey.ContainsKey(runKey))
                    continue;

                _feedByRunKey[runKey] = new GhaWorkflowLiveFeedState
                {
                    ConnectorName = entry.ConnectorName,
                    Owner = entry.OrgName ?? string.Empty,
                    RepositoryName = entry.RepositoryName,
                    RunId = status.RunId.Value,
                    WorkflowDisplayName = status.WorkflowName
                };

                overlayTerminal.Append($"gha:{entry.RepositoryName}", WorkerCommandStreamKind.Stdout, $"Run #{status.RunId.Value} - subscribing to job updates...");
            }
        }
    }

    private async Task PumpLiveFeedIntoOverlayAsync(CancellationToken cancellationToken)
    {
        foreach (var feed in _feedByRunKey.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var update = await liveFeedService.PollOnceAsync(feed, cancellationToken);
            if (update.NewLines.Count == 0)
                continue;

            var label = $"gha:{feed.RepositoryName}";
            foreach (var line in update.NewLines)
                overlayTerminal.Append(label, WorkerCommandStreamKind.Stdout, line);
        }
    }
}
