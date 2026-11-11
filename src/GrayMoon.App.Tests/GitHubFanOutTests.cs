using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Ci;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.GitHub;
using GrayMoon.App.Services.Ui;
using GrayMoon.Application.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>
/// The bounded GitHub fan-outs: push run watch (workflow discovery and live feed pump) and the connector repository
/// refresh. Every item is processed, results are applied in a deterministic order, the number of in-flight GitHub
/// calls reaches but never exceeds MaxParallelOperations, and one failing item leaves the others intact.
/// </summary>
public sealed partial class GitHubFanOutTests
{
    private const int ItemCount = 6;
    private const long RunIdBase = 100;

    // ---- Push run watch: workflow discovery ----

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Discovery_peak_concurrency_reaches_but_never_exceeds_the_limit(int limit)
    {
        var handler = new FanOutProbeHandler(FanOutProbeHandler.IsRunsRequest, Math.Min(limit, ItemCount));
        await using var rig = await FanOutRig.CreateAsync(handler, limit, ItemCount, connectorCount: 1);
        var watch = rig.CreateWatch(limit, out _);

        await watch.TickAsync(rig.PushedRepos, await rig.LoadLinksAsync(), CancellationToken.None);

        Assert.Equal(Math.Min(limit, ItemCount), handler.PeakInFlight);
        Assert.Equal(ItemCount, handler.TrackedCount);
    }

    [Fact]
    public async Task Discovery_processes_every_repo_and_writes_overlay_lines_in_repo_order()
    {
        var handler = new FanOutProbeHandler(FanOutProbeHandler.IsRunsRequest, gateTarget: 3);
        await using var rig = await FanOutRig.CreateAsync(handler, 3, ItemCount, connectorCount: 1);
        var watch = rig.CreateWatch(3, out var overlay);

        await watch.TickAsync(rig.PushedRepos, await rig.LoadLinksAsync(), CancellationToken.None);

        var lines = overlay.GetSnapshot();
        var subscribing = lines.Where(l => l.Text.Contains("subscribing", StringComparison.Ordinal)).Select(l => l.StreamLabel).ToList();
        Assert.Equal(Enumerable.Range(0, ItemCount).Select(i => $"gha:r{i}"), subscribing);

        var live = lines.Where(l => l.Text.StartsWith("Live - 1 job(s)", StringComparison.Ordinal)).Select(l => l.StreamLabel).ToList();
        Assert.Equal(Enumerable.Range(0, ItemCount).Select(i => $"gha:r{i}"), live);
    }

    [Fact]
    public async Task Discovery_failure_for_one_repo_leaves_the_others_intact()
    {
        var handler = new FanOutProbeHandler(FanOutProbeHandler.IsRunsRequest, gateTarget: 0) { FailRepoIndex = 2 };
        await using var rig = await FanOutRig.CreateAsync(handler, 3, 5, connectorCount: 1);
        var watch = rig.CreateWatch(3, out var overlay);

        await watch.TickAsync(rig.PushedRepos, await rig.LoadLinksAsync(), CancellationToken.None);

        var subscribing = overlay.GetSnapshot()
            .Where(l => l.Text.Contains("subscribing", StringComparison.Ordinal))
            .Select(l => l.StreamLabel)
            .ToList();
        Assert.Equal(["gha:r0", "gha:r1", "gha:r3", "gha:r4"], subscribing);
    }

    // ---- Push run watch: live feed pump ----

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Pump_peak_concurrency_reaches_but_never_exceeds_the_limit(int limit)
    {
        var handler = new FanOutProbeHandler(FanOutProbeHandler.IsJobsRequest, Math.Min(limit, ItemCount));
        await using var rig = await FanOutRig.CreateAsync(handler, limit, ItemCount, connectorCount: 1);
        var watch = rig.CreateWatch(limit, out _);

        await watch.TickAsync(rig.PushedRepos, await rig.LoadLinksAsync(), CancellationToken.None);

        Assert.Equal(Math.Min(limit, ItemCount), handler.PeakInFlight);
        Assert.Equal(ItemCount, handler.TrackedCount);
    }

    [Fact]
    public async Task Pump_failure_for_one_run_leaves_the_others_intact_and_keeps_order()
    {
        var handler = new FanOutProbeHandler(FanOutProbeHandler.IsJobsRequest, gateTarget: 0) { FailRunIndex = 2 };
        await using var rig = await FanOutRig.CreateAsync(handler, 3, 5, connectorCount: 1);
        var watch = rig.CreateWatch(3, out var overlay);

        await watch.TickAsync(rig.PushedRepos, await rig.LoadLinksAsync(), CancellationToken.None);

        var feedLines = overlay.GetSnapshot()
            .Where(l => !l.Text.Contains("subscribing", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"gha:r{i}"), feedLines.Select(l => l.StreamLabel));
        Assert.StartsWith("Update failed", feedLines[2].Text, StringComparison.Ordinal);
        Assert.All(feedLines.Where((_, i) => i != 2), l => Assert.StartsWith("Live - 1 job(s)", l.Text, StringComparison.Ordinal));
    }

    // ---- Connector repository refresh ----

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Refresh_peak_concurrency_reaches_but_never_exceeds_the_limit(int limit)
    {
        var handler = new FanOutProbeHandler(FanOutProbeHandler.IsUserReposRequest, Math.Min(limit, ItemCount));
        await using var rig = await FanOutRig.CreateAsync(handler, limit, repoCount: 0, connectorCount: ItemCount);
        await using var scope = rig.Provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<GitHubRepositoryService>();

        var result = await service.RefreshRepositoriesAsync();

        Assert.Empty(result.ConnectorErrors);
        Assert.Equal(Math.Min(limit, ItemCount), handler.PeakInFlight);
        Assert.Equal(ItemCount, handler.TrackedCount);
    }

    [Fact]
    public async Task Refresh_merges_every_connector_in_connector_order_and_reports_progress()
    {
        var handler = new FanOutProbeHandler(FanOutProbeHandler.IsUserReposRequest, gateTarget: 3);
        await using var rig = await FanOutRig.CreateAsync(handler, 3, repoCount: 0, connectorCount: ItemCount);
        await using var scope = rig.Provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<GitHubRepositoryService>();
        var progress = new RecordingProgress();

        var result = await service.RefreshRepositoriesAsync(progress);

        Assert.Empty(result.ConnectorErrors);
        var names = await rig.ReadRepositoryNamesInInsertOrderAsync();
        Assert.Equal(Enumerable.Range(0, ItemCount).Select(i => $"repo{i}"), names);
        var reports = progress.Snapshot();
        Assert.Equal(0, reports[0]);
        Assert.Equal(ItemCount, reports.Max());
        Assert.Equal(reports.OrderBy(v => v), reports);
    }

    [Fact]
    public async Task Refresh_failure_for_one_connector_is_recorded_and_the_others_still_merge()
    {
        var handler = new FanOutProbeHandler(FanOutProbeHandler.IsUserReposRequest, gateTarget: 0) { FailConnectorIndex = 2 };
        await using var rig = await FanOutRig.CreateAsync(handler, 3, repoCount: 0, connectorCount: 5);
        await using var scope = rig.Provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<GitHubRepositoryService>();

        var result = await service.RefreshRepositoriesAsync();

        var error = Assert.Single(result.ConnectorErrors);
        Assert.Equal("c2", error.ConnectorName);
        Assert.Equal(["repo0", "repo1", "repo3", "repo4"], await rig.ReadRepositoryNamesInInsertOrderAsync());
        var statuses = await rig.ReadConnectorStatusesAsync();
        Assert.Equal("Error", statuses["c2"]);
        Assert.NotEqual("Error", statuses["c1"]);
    }

    [Fact]
    public async Task Refresh_failure_for_one_connector_keeps_that_connectors_repositories_and_their_rows()
    {
        // Review 2026-11-10 finding 1: the failing connector has rows the merge could destroy. They must survive,
        // while a connector that answered still loses the repository it no longer returns.
        var handler = new FanOutProbeHandler(FanOutProbeHandler.IsUserReposRequest, gateTarget: 0) { FailConnectorIndex = 0 };
        await using var rig = await FanOutRig.CreateAsync(handler, 3, repoCount: 0, connectorCount: 2);

        int failedRepositoryId, failedLinkId, staleRepositoryId;
        await using (var seedScope = rig.Provider.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var connectors = await db.Connectors.AsNoTracking().ToDictionaryAsync(c => c.ConnectorName, c => c.ConnectorId);

            var workspace = new Workspace { Name = "workspace" };
            db.Workspaces.Add(workspace);
            var failedRepository = new Repository
            {
                ConnectorId = connectors["c0"],
                RepositoryName = "kept-by-failed-connector",
                OrgName = "org",
                CloneUrl = "https://c0.example/org/kept-by-failed-connector.git",
                GitHubRepositoryId = 5000
            };
            var staleRepository = new Repository
            {
                ConnectorId = connectors["c1"],
                RepositoryName = "no-longer-returned",
                OrgName = "other",
                CloneUrl = "https://c1.example/other/no-longer-returned.git",
                GitHubRepositoryId = 5001
            };
            db.Repositories.AddRange(failedRepository, staleRepository);
            await db.SaveChangesAsync();

            var failedLink = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = failedRepository.RepositoryId, BranchName = "main" };
            var staleLink = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = staleRepository.RepositoryId, BranchName = "main" };
            db.WorkspaceRepositories.AddRange(failedLink, staleLink);
            await db.SaveChangesAsync();

            db.RepositoryBranches.Add(new RepositoryBranch { WorkspaceRepositoryId = failedLink.WorkspaceRepositoryId, BranchName = "main" });
            db.WorkspaceRepositoryPullRequests.Add(new WorkspaceRepositoryPullRequest
            {
                WorkspaceRepositoryId = failedLink.WorkspaceRepositoryId,
                PullRequestNumber = 7,
                State = "open",
                LastCheckedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            failedRepositoryId = failedRepository.RepositoryId;
            failedLinkId = failedLink.WorkspaceRepositoryId;
            staleRepositoryId = staleRepository.RepositoryId;
        }

        RefreshRepositoriesResult result;
        await using (var scope = rig.Provider.CreateAsyncScope())
            result = await scope.ServiceProvider.GetRequiredService<GitHubRepositoryService>().RefreshRepositoriesAsync();

        var error = Assert.Single(result.ConnectorErrors);
        Assert.Equal("c0", error.ConnectorName);
        Assert.StartsWith("Skipped:", error.Message, StringComparison.Ordinal);

        await using var verifyScope = rig.Provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await verifyDb.Repositories.AnyAsync(r => r.RepositoryId == failedRepositoryId));
        Assert.True(await verifyDb.WorkspaceRepositories.AnyAsync(l => l.WorkspaceRepositoryId == failedLinkId));
        Assert.True(await verifyDb.RepositoryBranches.AnyAsync(b => b.WorkspaceRepositoryId == failedLinkId));
        Assert.True(await verifyDb.WorkspaceRepositoryPullRequests.AnyAsync(p => p.WorkspaceRepositoryId == failedLinkId));

        // The connector that answered has its fetched repository merged. Its repository that is no longer returned
        // is linked to a Workspace, so it is kept too: connector state never removes a Workspace's repository.
        Assert.True(await verifyDb.Repositories.AnyAsync(r => r.RepositoryId == staleRepositoryId));
        Assert.True(await verifyDb.Repositories.AnyAsync(r => r.RepositoryName == "repo1"));
    }

    [Fact]
    public async Task Refresh_keeps_every_repository_of_a_connector_that_answers_with_none()
    {
        // A 200 with an empty list (a token that lost its organization grant) says nothing about the stored
        // repositories. Before the fix the other connector's answer ran the merge and deleted them all.
        var handler = new FanOutProbeHandler(FanOutProbeHandler.IsUserReposRequest, gateTarget: 0) { EmptyConnectorIndex = 0 };
        await using var rig = await FanOutRig.CreateAsync(handler, 3, repoCount: 0, connectorCount: 2);
        var linked = await SeedStoredRepositoryAsync(rig, "c0", "linked-on-empty-connector", 6000, linked: true);
        var unlinked = await SeedStoredRepositoryAsync(rig, "c0", "unlinked-on-empty-connector", 6001, linked: false);

        RefreshRepositoriesResult result;
        await using (var scope = rig.Provider.CreateAsyncScope())
            result = await scope.ServiceProvider.GetRequiredService<GitHubRepositoryService>().RefreshRepositoriesAsync();

        var error = Assert.Single(result.ConnectorErrors);
        Assert.Equal("c0", error.ConnectorName);
        Assert.Contains("returned no repositories", error.Message, StringComparison.Ordinal);

        await AssertStoredRepositorySurvivedAsync(rig, linked);
        await AssertStoredRepositorySurvivedAsync(rig, unlinked);
        await using var verifyScope = rig.Provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await verifyDb.Repositories.AnyAsync(r => r.RepositoryName == "repo1"));
        // An empty answer is not a connector failure: the connector is not marked as broken.
        Assert.NotEqual("Error", (await rig.ReadConnectorStatusesAsync())["c0"]);
    }

    [Fact]
    public async Task Refresh_keeps_every_repository_of_an_inactive_connector()
    {
        // An inactive connector is never asked, so its repositories are not "no longer returned".
        var handler = new FanOutProbeHandler(FanOutProbeHandler.IsUserReposRequest, gateTarget: 0);
        await using var rig = await FanOutRig.CreateAsync(handler, 3, repoCount: 0, connectorCount: 2);
        var linked = await SeedStoredRepositoryAsync(rig, "c0", "linked-on-inactive-connector", 6100, linked: true);
        var unlinked = await SeedStoredRepositoryAsync(rig, "c0", "unlinked-on-inactive-connector", 6101, linked: false);
        await using (var seedScope = rig.Provider.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Connectors.SingleAsync(c => c.ConnectorName == "c0")).IsActive = false;
            await db.SaveChangesAsync();
        }

        RefreshRepositoriesResult result;
        await using (var scope = rig.Provider.CreateAsyncScope())
            result = await scope.ServiceProvider.GetRequiredService<GitHubRepositoryService>().RefreshRepositoriesAsync();

        Assert.Empty(result.ConnectorErrors);
        await AssertStoredRepositorySurvivedAsync(rig, linked);
        await AssertStoredRepositorySurvivedAsync(rig, unlinked);
    }

    [Fact]
    public async Task Refresh_where_every_connector_answers_with_none_changes_nothing_and_names_them()
    {
        var handler = new FanOutProbeHandler(FanOutProbeHandler.IsUserReposRequest, gateTarget: 0) { AllConnectorsEmpty = true };
        await using var rig = await FanOutRig.CreateAsync(handler, 3, repoCount: 0, connectorCount: 2);
        var linked = await SeedStoredRepositoryAsync(rig, "c0", "linked", 6200, linked: true);
        var unlinked = await SeedStoredRepositoryAsync(rig, "c0", "unlinked", 6201, linked: false);

        RefreshRepositoriesResult result;
        await using (var scope = rig.Provider.CreateAsyncScope())
            result = await scope.ServiceProvider.GetRequiredService<GitHubRepositoryService>().RefreshRepositoriesAsync();

        // c1 has nothing stored, so its empty answer is a normal, silent success.
        var error = Assert.Single(result.ConnectorErrors);
        Assert.Equal("c0", error.ConnectorName);
        await AssertStoredRepositorySurvivedAsync(rig, linked);
        await AssertStoredRepositorySurvivedAsync(rig, unlinked);
    }

    [Fact]
    public async Task Refresh_deletes_an_unlinked_repository_and_keeps_and_marks_a_linked_one_no_longer_returned()
    {
        var handler = new FanOutProbeHandler(FanOutProbeHandler.IsUserReposRequest, gateTarget: 0);
        await using var rig = await FanOutRig.CreateAsync(handler, 3, repoCount: 0, connectorCount: 1);
        var linked = await SeedStoredRepositoryAsync(rig, "c0", "linked-gone", 6300, linked: true);
        var unlinked = await SeedStoredRepositoryAsync(rig, "c0", "unlinked-gone", 6301, linked: false);

        RefreshRepositoriesResult result;
        await using (var scope = rig.Provider.CreateAsyncScope())
            result = await scope.ServiceProvider.GetRequiredService<GitHubRepositoryService>().RefreshRepositoriesAsync();

        Assert.Empty(result.ConnectorErrors);
        await AssertStoredRepositorySurvivedAsync(rig, linked);
        var kept = Assert.Single(result.KeptMissingFromProvider);
        Assert.Equal(linked.RepositoryId, kept.RepositoryId);
        Assert.Equal(["workspace-linked-gone"], kept.WorkspaceNames);

        await using var verifyScope = rig.Provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotNull((await verifyDb.Repositories.AsNoTracking().SingleAsync(r => r.RepositoryId == linked.RepositoryId)).MissingFromProviderSinceUtc);
        Assert.False(await verifyDb.Repositories.AnyAsync(r => r.RepositoryId == unlinked.RepositoryId));
        Assert.Null((await verifyDb.Repositories.AsNoTracking().SingleAsync(r => r.RepositoryName == "repo0")).MissingFromProviderSinceUtc);
    }

    private sealed record StoredRepository(int RepositoryId, int? LinkId);

    /// <summary>
    /// Stores a repository for <paramref name="connectorName"/> that no fetch returns. A linked one gets its own
    /// Workspace, a branch row and a pull request row, so a wrong delete has dependent rows to destroy.
    /// </summary>
    private static async Task<StoredRepository> SeedStoredRepositoryAsync(FanOutRig rig, string connectorName, string name, long providerId, bool linked)
    {
        await using var scope = rig.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connectorId = (await db.Connectors.AsNoTracking().SingleAsync(c => c.ConnectorName == connectorName)).ConnectorId;
        var repository = new Repository
        {
            ConnectorId = connectorId,
            RepositoryName = name,
            OrgName = "stored",
            CloneUrl = $"https://{connectorName}.example/stored/{name}.git",
            GitHubRepositoryId = providerId
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        if (!linked)
            return new StoredRepository(repository.RepositoryId, null);

        var workspace = new Workspace { Name = "workspace-" + name };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var link = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repository.RepositoryId, BranchName = "main" };
        db.WorkspaceRepositories.Add(link);
        await db.SaveChangesAsync();
        db.RepositoryBranches.Add(new RepositoryBranch { WorkspaceRepositoryId = link.WorkspaceRepositoryId, BranchName = "main" });
        db.WorkspaceRepositoryPullRequests.Add(new WorkspaceRepositoryPullRequest
        {
            WorkspaceRepositoryId = link.WorkspaceRepositoryId,
            PullRequestNumber = 7,
            State = "open",
            LastCheckedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return new StoredRepository(repository.RepositoryId, link.WorkspaceRepositoryId);
    }

    private static async Task AssertStoredRepositorySurvivedAsync(FanOutRig rig, StoredRepository stored)
    {
        await using var scope = rig.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Repositories.AnyAsync(r => r.RepositoryId == stored.RepositoryId));
        if (stored.LinkId is not { } linkId)
            return;
        Assert.True(await db.WorkspaceRepositories.AnyAsync(l => l.WorkspaceRepositoryId == linkId));
        Assert.True(await db.RepositoryBranches.AnyAsync(b => b.WorkspaceRepositoryId == linkId));
        Assert.True(await db.WorkspaceRepositoryPullRequests.AnyAsync(p => p.WorkspaceRepositoryId == linkId));
    }

    private sealed class RecordingProgress : IProgress<int>
    {
        private readonly object _lock = new();
        private readonly List<int> _values = [];

        public void Report(int value)
        {
            lock (_lock)
                _values.Add(value);
        }

        public List<int> Snapshot()
        {
            lock (_lock)
                return _values.ToList();
        }
    }

    /// <summary>
    /// Serves the GitHub endpoints the fan-outs call. Requests matching the tracked predicate are counted as in flight;
    /// the first <c>gateTarget</c> of them wait for each other so a correct bound shows up as a peak of exactly that
    /// many, and every tracked request holds for a short, per-item varied time so any excess would overlap.
    /// </summary>
    private sealed partial class FanOutProbeHandler(Func<Uri, bool> isTracked, int gateTarget) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _peak;
        private int _arrived;
        private int _tracked;

        public int FailRepoIndex { get; init; } = -1;
        public int FailRunIndex { get; init; } = -1;
        public int FailConnectorIndex { get; init; } = -1;

        /// <summary>This connector answers 200 with an empty list (a token that lost its organization grant answers like this).</summary>
        public int EmptyConnectorIndex { get; init; } = -1;
        public bool AllConnectorsEmpty { get; init; }
        public int PeakInFlight => Volatile.Read(ref _peak);
        public int TrackedCount => Volatile.Read(ref _tracked);

        public static bool IsRunsRequest(Uri uri) =>
            uri.PathAndQuery.Contains("/actions/runs?branch=", StringComparison.Ordinal);

        public static bool IsJobsRequest(Uri uri) =>
            uri.PathAndQuery.Contains("/jobs", StringComparison.Ordinal);

        public static bool IsUserReposRequest(Uri uri) =>
            uri.AbsolutePath.StartsWith("/user/repos", StringComparison.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (!isTracked(uri))
                return Respond(uri);

            Interlocked.Increment(ref _tracked);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _peak)))
                Interlocked.CompareExchange(ref _peak, now, seen);

            try
            {
                if (gateTarget > 0)
                {
                    if (Interlocked.Increment(ref _arrived) >= gateTarget)
                        _gate.TrySetResult();
                    await Task.WhenAny(_gate.Task, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
                }

                // Earlier items take longest, so completion order is the reverse of item order.
                await Task.Delay(10 + Math.Max(0, ItemCount - ItemIndex(uri)) * 10, cancellationToken);
                return Respond(uri);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static int ItemIndex(Uri uri)
        {
            var match = ItemIndexPattern().Match(uri.Host + uri.AbsolutePath);
            if (!match.Success)
                return 0;
            if (match.Groups["run"].Success)
                return (int)(long.Parse(match.Groups["run"].Value) - RunIdBase);
            return int.Parse(match.Groups["repo"].Success ? match.Groups["repo"].Value : match.Groups["conn"].Value);
        }

        [GeneratedRegex(@"^(c(?<conn>\d+)\.example|.*?/repos/acme/r(?<repo>\d+)/|.*?/runs/(?<run>\d+)/jobs)")]
        private static partial Regex ItemIndexPattern();

        private HttpResponseMessage Respond(Uri uri)
        {
            var path = uri.AbsolutePath;
            var pathAndQuery = uri.PathAndQuery;

            if (IsUserReposRequest(uri))
            {
                var index = ItemIndex(uri);
                if (index == FailConnectorIndex)
                    return Plain(HttpStatusCode.InternalServerError);
                if (index == EmptyConnectorIndex || AllConnectorsEmpty)
                    return Json("[]");

                return Json($$"""[{"id":{{1000 + index}},"node_id":"n{{index}}","name":"repo{{index}}","full_name":"org/repo{{index}}","private":false,"archived":false,"clone_url":"https://c{{index}}.example/org/repo{{index}}.git","owner":{"login":"org"},"topics":[]}]""");
            }

            if (path.EndsWith("/actions/workflows", StringComparison.Ordinal))
                return Json("""{"workflows":[{"id":1,"name":"build","path":".github/workflows/build.yml","html_url":"","state":"active"}]}""");

            if (pathAndQuery.Contains("/actions/runs?branch=", StringComparison.Ordinal))
            {
                var index = ItemIndex(uri);
                if (index == FailRepoIndex)
                    return Plain(HttpStatusCode.InternalServerError);

                return Json($$"""{"workflow_runs":[{"id":{{RunIdBase + index}},"workflow_id":1,"name":"build","event":"push","head_branch":"main","status":"in_progress","conclusion":null,"updated_at":"2026-10-06T00:00:00Z","html_url":""}]}""");
            }

            if (path.Contains("/jobs", StringComparison.Ordinal))
            {
                if (ItemIndex(uri) == FailRunIndex)
                    return Plain(HttpStatusCode.InternalServerError);

                return Json("""{"total_count":1,"jobs":[{"id":5,"name":"build","status":"in_progress","conclusion":null,"steps":[{"name":"compile","status":"in_progress","conclusion":null,"number":1}]}]}""");
            }

            return Plain(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Plain(HttpStatusCode status) =>
            new(status) { Content = new StringContent("{}") };
    }

    private sealed class FanOutRig : IAsyncDisposable
    {
        private readonly SqliteConnection _keepAlive;

        private FanOutRig(SqliteConnection keepAlive, ServiceProvider provider, IReadOnlyList<int> repositoryIds)
        {
            _keepAlive = keepAlive;
            Provider = provider;
            PushedRepos = repositoryIds.Select((id, i) => new PushRepoPayload(id, $"r{i}", 1, [])).ToList();
        }

        public ServiceProvider Provider { get; }
        public IReadOnlyList<PushRepoPayload> PushedRepos { get; }
        public int WorkspaceId { get; private set; }

        public static async Task<FanOutRig> CreateAsync(FanOutProbeHandler handler, int limit, int repoCount, int connectorCount)
        {
            var connectionString = $"Data Source=file:fo{Guid.NewGuid():N}?mode=memory&cache=shared;Pooling=False;Default Timeout=5";
            var keepAlive = new SqliteConnection(connectionString);
            await keepAlive.OpenAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.Configure<WorkspaceOptions>(o => o.MaxParallelOperations = limit);
            services.AddMemoryCache();
            services.AddSingleton<IGitHubRateLimitTracker, GitHubRateLimitTracker>();
            services.AddSingleton<IGitHubETagCache, GitHubETagCache>();
            services.AddSingleton<IGitHubApiUsageRecorder, FakeGitHubApiUsageRecorder>();
            services.AddSingleton<IGhaLiveFeedJobsCache, GhaLiveFeedJobsCache>();
            services.AddSingleton<IWorkspaceGitChangesNotifier, RecordingGitChangesNotifier>();
            services.AddSingleton(new HttpClient(handler));
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString), ServiceLifetime.Scoped);
            services.AddScoped<ConnectorRepository>();
            services.AddScoped<GitHubRepositoryRepository>();
            services.AddScoped<GitHubService>();
            services.AddScoped<GitHubRepositoryService>();
            services.AddScoped<GitHubActionsService>();
            services.AddScoped<GhaWorkflowLiveFeedService>();
            var provider = services.BuildServiceProvider();

            IReadOnlyList<int> repositoryIds;
            int workspaceId;
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.CreateSchemaAsync();

                var ids = new List<int>();
                if (repoCount > 0)
                {
                    var connector = new Connector
                    {
                        ConnectorName = "GitHub",
                        ConnectorType = ConnectorType.GitHub,
                        ApiBaseUrl = "https://api.github.com/",
                        UserToken = "test-token",
                        IsActive = true
                    };
                    db.Connectors.Add(connector);
                    await db.SaveChangesAsync();

                    var workspace = new Workspace
                    {
                        Name = "workspace",
                        Type = WorkspaceType.DotNetDependency,
                        VersioningMode = WorkspaceVersioningMode.GitVersion,
                        CiProvider = WorkspaceCiProvider.GitHubActions
                    };
                    db.Workspaces.Add(workspace);
                    await db.SaveChangesAsync();
                    workspaceId = workspace.WorkspaceId;

                    for (var i = 0; i < repoCount; i++)
                    {
                        var repository = new Repository
                        {
                            ConnectorId = connector.ConnectorId,
                            RepositoryName = $"r{i}",
                            OrgName = "acme",
                            CloneUrl = $"https://github.com/acme/r{i}.git"
                        };
                        db.Repositories.Add(repository);
                        await db.SaveChangesAsync();
                        db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
                        {
                            WorkspaceId = workspaceId,
                            RepositoryId = repository.RepositoryId,
                            BranchName = "main"
                        });
                        await db.SaveChangesAsync();
                        ids.Add(repository.RepositoryId);
                    }
                }
                else
                {
                    workspaceId = 0;
                }

                for (var i = 0; i < connectorCount && repoCount == 0; i++)
                {
                    db.Connectors.Add(new Connector
                    {
                        ConnectorName = $"c{i}",
                        ConnectorType = ConnectorType.GitHub,
                        ApiBaseUrl = $"https://c{i}.example/",
                        UserToken = "test-token",
                        IsActive = true
                    });
                }

                await db.SaveChangesAsync();
                repositoryIds = ids;
            }

            return new FanOutRig(keepAlive, provider, repositoryIds) { WorkspaceId = workspaceId };
        }

        public GitHubActionsPushRunWatch CreateWatch(int limit, out OverlayCommandTerminalService overlay)
        {
            overlay = new OverlayCommandTerminalService();
            return new GitHubActionsPushRunWatch(
                Provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new WorkspaceOptions { MaxParallelOperations = limit }),
                overlay,
                NullLogger.Instance);
        }

        public async Task<IReadOnlyList<WorkspaceRepositoryLink>> LoadLinksAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.WorkspaceRepositories
                .AsNoTracking()
                .Include(wr => wr.Repository)
                .ThenInclude(r => r!.Connector)
                .Where(wr => wr.WorkspaceId == WorkspaceId)
                .OrderBy(wr => wr.WorkspaceRepositoryId)
                .ToListAsync();
        }

        public async Task<List<string>> ReadRepositoryNamesInInsertOrderAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Repositories
                .AsNoTracking()
                .OrderBy(r => r.RepositoryId)
                .Select(r => r.RepositoryName)
                .ToListAsync();
        }

        public async Task<Dictionary<string, string>> ReadConnectorStatusesAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Connectors
                .AsNoTracking()
                .ToDictionaryAsync(c => c.ConnectorName, c => c.Status ?? string.Empty);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await _keepAlive.DisposeAsync();
        }
    }
}
