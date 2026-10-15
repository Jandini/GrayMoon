using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Models;
using GrayMoon.App.Services.GitHub;
using GrayMoon.App.Services.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.Ci;

/// <summary>
/// GitHub Actions run watching during synchronized push: every few seconds discovers running workflows on the
/// branches already pushed, then streams their job/step transitions into the push overlay terminal. Moved out of
/// <see cref="WorkspacePushService"/> unchanged; one instance per dependency level, like the state it replaced.
/// GitHub lookups fan out (bounded by <see cref="WorkspaceOptions.MaxParallelOperations"/>), each in its own DI scope
/// so no scoped DbContext is shared between parallel calls; results are applied to the state and the overlay
/// sequentially, in the original order.
/// </summary>
internal sealed class GitHubActionsPushRunWatch(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkspaceOptions> workspaceOptions,
    OverlayCommandTerminalService overlayTerminal,
    ILogger logger) : IPushCiRunWatch
{
    private const int DiscoveryIntervalSeconds = 6;

    private readonly Dictionary<string, GhaWorkflowLiveFeedState> _feedByRunKey = new(StringComparer.Ordinal);
    private readonly HashSet<int> _noWorkflowRepoIds = [];
    private bool _discoveryEnabled = true;
    private DateTime _lastDiscoveryUtc = DateTime.MinValue;
    private DateTime _lastPollUtc = DateTime.MinValue;

    private int MaxConcurrency => Math.Max(1, workspaceOptions.Value.MaxParallelOperations);

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
        var candidates = new List<(int RepoId, GitHubRepositoryEntry Entry, string Branch)>();
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

            candidates.Add((repo.RepoId, entry, link.BranchName!));
        }

        if (candidates.Count == 0)
            return;

        // Fan out the GitHub lookups, then apply every result on this one thread in the original order.
        using var gate = new SemaphoreSlim(MaxConcurrency);
        var lookups = await Task.WhenAll(candidates.Select(c => LookUpWorkflowsAsync(c.RepoId, c.Entry, c.Branch, gate, cancellationToken)));

        for (var i = 0; i < candidates.Count; i++)
        {
            var (repoId, entry, _) = candidates[i];
            var lookup = lookups[i];
            if (lookup.Failed)
                continue;

            var statuses = lookup.Statuses;
            if (statuses == null || statuses.Count == 0)
            {
                _noWorkflowRepoIds.Add(repoId);
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

    private async Task<WorkflowLookup> LookUpWorkflowsAsync(
        int repoId,
        GitHubRepositoryEntry entry,
        string branch,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Own scope per lookup: the service reads connectors through a scoped DbContext that must not be shared across parallel calls.
            await using var scope = scopeFactory.CreateAsyncScope();
            var gitHubActionsService = scope.ServiceProvider.GetRequiredService<GitHubActionsService>();
            var statuses = await gitHubActionsService.GetWorkflowStatusesForBranchAsync(entry, branch, cancellationToken: cancellationToken);
            return new WorkflowLookup(false, statuses);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Push wait: failed to discover running workflows for repo {RepoId}", repoId);
            return new WorkflowLookup(true, null);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PumpLiveFeedIntoOverlayAsync(CancellationToken cancellationToken)
    {
        var feeds = _feedByRunKey.Values.ToList();
        using var gate = new SemaphoreSlim(MaxConcurrency);
        var updates = await Task.WhenAll(feeds.Select(feed => PollFeedAsync(feed, gate, cancellationToken)));

        for (var i = 0; i < feeds.Count; i++)
        {
            var update = updates[i];
            if (update == null || update.NewLines.Count == 0)
                continue;

            var label = $"gha:{feeds[i].RepositoryName}";
            foreach (var line in update.NewLines)
                overlayTerminal.Append(label, WorkerCommandStreamKind.Stdout, line);
        }
    }

    private async Task<GhaWorkflowLiveFeedUpdate?> PollFeedAsync(
        GhaWorkflowLiveFeedState feed,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Own scope per poll: the first poll resolves the connector through a scoped DbContext.
            await using var scope = scopeFactory.CreateAsyncScope();
            var liveFeedService = scope.ServiceProvider.GetRequiredService<GhaWorkflowLiveFeedService>();
            return await liveFeedService.PollOnceAsync(feed, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Push wait: live feed poll failed for run {RunId}", feed.RunId);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record WorkflowLookup(bool Failed, IReadOnlyList<ActionStatusInfo>? Statuses);
}
