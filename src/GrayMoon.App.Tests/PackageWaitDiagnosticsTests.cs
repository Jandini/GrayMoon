using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Connectors;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Tests;

public sealed class PackageWaitDiagnosticsTests
{
    private const string NuGetIndexUrl = "https://nuget.test/v3/index.json";
    private const string SecretToken = "super-secret-token";

    [Fact]
    public async Task Lookup_distinguishes_absence_from_registry_errors_without_changing_the_exists_decision()
    {
        var handler = new ScriptedNuGetHandler();
        var service = new NuGetService(new HttpClient(handler), Microsoft.Extensions.Logging.Abstractions.NullLogger<NuGetService>.Instance);
        var connector = Connector();

        handler.PackageStatus = HttpStatusCode.NotFound;
        var missing = await service.LookupPackageVersionAsync(connector, "Acme.Missing", "1.0.0");
        Assert.False(missing.Exists);
        Assert.Equal(PackageVersionLookupKind.VersionNotFound, missing.Kind);

        handler.PackageStatus = HttpStatusCode.Unauthorized;
        var denied = await service.LookupPackageVersionAsync(connector, "Acme.Missing", "1.0.0");
        Assert.False(denied.Exists);
        Assert.Equal(PackageVersionLookupKind.AuthenticationFailed, denied.Kind);

        handler.PackageStatus = HttpStatusCode.ServiceUnavailable;
        var failed = await service.LookupPackageVersionAsync(connector, "Acme.Missing", "1.0.0");
        Assert.False(failed.Exists);
        Assert.Equal(PackageVersionLookupKind.RegistryFailure, failed.Kind);

        handler.PackageStatus = HttpStatusCode.OK;
        handler.VersionsByPackage["acme.missing"] = ["1.0.0"];
        var found = await service.LookupPackageVersionAsync(connector, "Acme.Missing", "1.0.0");
        Assert.True(found.Exists);
        Assert.Equal(PackageVersionLookupKind.Available, found.Kind);
        Assert.True(await service.PackageVersionExistsAsync(connector, "Acme.Missing", "1.0.0"));

        handler.VersionsByPackage["acme.missing"] = ["9.9.9"];
        var wrongVersion = await service.LookupPackageVersionAsync(connector, "Acme.Missing", "1.0.0");
        Assert.False(wrongVersion.Exists);
        Assert.Equal(PackageVersionLookupKind.VersionNotFound, wrongVersion.Kind);

        handler.PackageBody = """{"not":"versions"}""";
        var unexpected = await service.LookupPackageVersionAsync(connector, "Acme.Missing", "1.0.0");
        Assert.False(unexpected.Exists);
        Assert.Equal(PackageVersionLookupKind.UnexpectedResponse, unexpected.Kind);
    }

    [Fact]
    public void Failed_workflow_correlates_only_by_commit_sha_and_does_not_prove_publication()
    {
        const string sha = "abc123def456";
        var diagnostic = PackageWaitDiagnostics.Correlate(
            sha,
            PackageWaitDiagnostics.HeadCommitSource,
            [
                new GitHubWorkflowRunDto
                {
                    Id = 11,
                    Name = "CI",
                    HeadSha = "someone-elses-commit",
                    Status = "completed",
                    Conclusion = "success",
                    HtmlUrl = "https://github.com/acme/lib/actions/runs/11",
                    UpdatedAt = DateTimeOffset.Parse("2026-10-21T12:00:00Z")
                },
                new GitHubWorkflowRunDto
                {
                    Id = 42,
                    Name = "publish",
                    HeadSha = sha,
                    Status = "completed",
                    Conclusion = "failure",
                    HtmlUrl = "https://github.com/acme/lib/actions/runs/42",
                    UpdatedAt = DateTimeOffset.Parse("2026-10-21T12:05:00Z")
                }
            ],
            lookupAttempted: true,
            lookupFailed: false);

        Assert.Equal(PackageWaitDiagnostics.WorkflowFailed, diagnostic.WorkflowStatus);
        Assert.Equal(42, diagnostic.WorkflowRunId);
        Assert.Equal("https://github.com/acme/lib/actions/runs/42", diagnostic.WorkflowRunUrl);
        Assert.Equal(PackageWaitDiagnostics.PublicationUnverified, diagnostic.PublicationStatus);
        Assert.True(diagnostic.CorrelatedToCommit);

        var logs = new CapturingLoggerProvider();
        PackageWaitDiagnostics.LogPublication(
            logs.CreateLogger("test"),
            "bd7498ca",
            "AuroraVerityReview.Contracts.DocumentSearch",
            "1.0.0-BAM-3142-progress-count-errors.5",
            "document-search",
            "Succeeded",
            diagnostic);

        var line = Assert.Single(logs.Lines, text => text.Contains("Package publication diagnostics", StringComparison.Ordinal));
        Assert.Contains("WorkflowStatus=WorkflowFailed", line, StringComparison.Ordinal);
        Assert.Contains("WorkflowRunUrl=https://github.com/acme/lib/actions/runs/42", line, StringComparison.Ordinal);
        Assert.Contains("PublicationStatus=Unverified", line, StringComparison.Ordinal);
        Assert.Contains("ProducerPushStatus=Succeeded", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Published", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_lookup_failure_does_not_escape_and_does_not_call_without_a_commit()
    {
        var called = 0;
        var withoutSha = await PackageWaitDiagnostics.ReadWorkflowAsync(
            _ =>
            {
                called++;
                return Task.FromResult<IReadOnlyList<GitHubWorkflowRunDto>>([]);
            },
            commitSha: null,
            PackageWaitDiagnostics.HeadCommitSource,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        Assert.Equal(0, called);
        Assert.Equal(PackageWaitDiagnostics.WorkflowCorrelationUnknown, withoutSha.WorkflowStatus);

        var failed = await PackageWaitDiagnostics.ReadWorkflowAsync(
            _ => throw new InvalidOperationException("github down"),
            "abc123",
            PackageWaitDiagnostics.HeadCommitSource,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        Assert.Equal(PackageWaitDiagnostics.WorkflowStatusUnavailable, failed.WorkflowStatus);
        Assert.Equal(PackageWaitDiagnostics.PublicationUnverified, failed.PublicationStatus);

        var hung = await PackageWaitDiagnostics.ReadWorkflowAsync(
            async token =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5), token);
                return [];
            },
            "abc123",
            PackageWaitDiagnostics.HeadCommitSource,
            TimeSpan.FromMilliseconds(200),
            CancellationToken.None);
        Assert.Equal(PackageWaitDiagnostics.WorkflowStatusUnavailable, hung.WorkflowStatus);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var stopped = await PackageWaitDiagnostics.ReadWorkflowAsync(
            async token =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5), token);
                return [];
            },
            "abc123",
            PackageWaitDiagnostics.HeadCommitSource,
            TimeSpan.FromSeconds(5),
            canceled.Token);
        Assert.Equal(PackageWaitDiagnostics.WorkflowStatusUnavailable, stopped.WorkflowStatus);
    }

    [Fact]
    public async Task Missing_package_version_is_named_in_the_timeout_log_and_the_wait_still_fails()
    {
        var nuget = new ScriptedNuGetHandler();
        nuget.VersionsByPackage["found.a"] = ["1.2.3"];
        nuget.VersionsByPackage["found.b"] = ["1.2.3"];
        var logs = new CapturingLoggerProvider();
        await using var ctx = await CreateContextAsync(nuget, logs);
        var graph = await SeedGraphAsync(ctx,
        [
            new GraphRepo("producer-a", 1, "Found.A", "1.2.3"),
            new GraphRepo("producer-b", 1, "Found.B", "1.2.3"),
            new GraphRepo("producer-c", 1, "Missing.C", "1.2.3"),
            new GraphRepo("consumer-one", 2, null, "2.0.0"),
            new GraphRepo("consumer-two", 2, null, "2.0.0")
        ],
        [
            new GraphEdge("consumer-one", "Found.A", "1.2.3"),
            new GraphEdge("consumer-one", "Found.B", "1.2.3"),
            new GraphEdge("consumer-one", "Missing.C", "1.2.3"),
            new GraphEdge("consumer-two", "Found.A", "1.2.3"),
            new GraphEdge("consumer-two", "Found.B", "1.2.3"),
            new GraphEdge("consumer-two", "Missing.C", "1.2.3")
        ]);

        var errors = await RunPushAsync(ctx, "bd7498ca");

        var timeout = Assert.Single(errors);
        Assert.Equal(2, timeout.Level);
        Assert.Contains("Timed out waiting for package dependencies", timeout.Message, StringComparison.Ordinal);
        Assert.Contains("2 of 3", timeout.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cancelled", timeout.Message, StringComparison.OrdinalIgnoreCase);

        var text = logs.Text;
        Assert.Contains("Package readiness wait started", text, StringComparison.Ordinal);
        Assert.Contains("PackageId=Missing.C", text, StringComparison.Ordinal);
        Assert.Contains("ExpectedVersion=1.2.3", text, StringComparison.Ordinal);
        Assert.Contains("RegistryName=nuget-test", text, StringComparison.Ordinal);
        Assert.Contains($"ConnectorId={graph.ConnectorId}", text, StringComparison.Ordinal);
        Assert.Contains("DependencyLevel=2", text, StringComparison.Ordinal);
        Assert.Contains("AvailablePackages=2", text, StringComparison.Ordinal);
        Assert.Contains("MissingPackages=1", text, StringComparison.Ordinal);
        Assert.Contains("consumer-one", text, StringComparison.Ordinal);
        Assert.Contains("consumer-two", text, StringComparison.Ordinal);
        Assert.Contains("ProducerRepository=producer-c", text, StringComparison.Ordinal);
        Assert.Contains("ProducerStatus=Succeeded", text, StringComparison.Ordinal);
        Assert.Contains("LastLookupResult=VersionNotFound", text, StringComparison.Ordinal);
        Assert.Contains("FailureCategory=PackageWaitTimeout", text, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/runs", text, StringComparison.Ordinal);
        Assert.Equal(3, ctx.WorkerBridge.Calls.Count(call => call.Command == "PushRepository"));
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, call => call.Command == "PushRepository" && Args(call.Args).GetProperty("repositoryName").GetString() is "consumer-one" or "consumer-two");
    }

    [Fact]
    public async Task Delayed_publication_logs_the_availability_transition_once_and_does_not_time_out()
    {
        var nuget = new ScriptedNuGetHandler { PackageFailuresRemaining = 1, FailureStatus = HttpStatusCode.NotFound };
        nuget.VersionsByPackage["found.a"] = ["1.2.3"];
        var logs = new CapturingLoggerProvider();
        await using var ctx = await CreateContextAsync(nuget, logs);
        await SeedGraphAsync(ctx,
            [new GraphRepo("producer-a", 1, "Found.A", "1.2.3"), new GraphRepo("consumer-one", 2, null, "2.0.0")],
            [new GraphEdge("consumer-one", "Found.A", "1.2.3")]);

        var errors = await RunPushAsync(ctx, "delay");

        Assert.Empty(errors);
        var available = logs.Lines.Where(line => line.Contains("LastLookupResult=Available", StringComparison.Ordinal)).ToList();
        Assert.Single(available);
        Assert.Contains(logs.Lines, line => line.Contains("Package readiness changed", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Text, "Push dependency wait timed out", StringComparison.Ordinal);
        Assert.Contains(ctx.WorkerBridge.Calls, call => call.Command == "PushRepository" && Args(call.Args).GetProperty("repositoryName").GetString() == "consumer-one");
    }

    [Fact]
    public async Task Version_mismatch_is_logged_from_its_sources_and_readiness_still_uses_the_dependency_version()
    {
        var nuget = new ScriptedNuGetHandler();
        nuget.VersionsByPackage["found.a"] = ["1.0.0-feature.5"];
        var logs = new CapturingLoggerProvider();
        await using var ctx = await CreateContextAsync(nuget, logs);
        await SeedGraphAsync(ctx,
            [new GraphRepo("producer-a", 1, "Found.A", "1.0.0"), new GraphRepo("consumer-one", 2, null, "2.0.0")],
            [new GraphEdge("consumer-one", "Found.A", "1.0.0-feature.5")]);

        var errors = await RunPushAsync(ctx, "mismatch");

        Assert.Empty(errors);
        var text = logs.Text;
        Assert.Contains("Expected package version differs from known version metadata", text, StringComparison.Ordinal);
        Assert.Contains("ExpectedVersion=1.0.0-feature.5", text, StringComparison.Ordinal);
        Assert.Contains("ExpectedVersionSource=ProjectDependency.Version", text, StringComparison.Ordinal);
        Assert.Contains("KnownVersion=1.0.0", text, StringComparison.Ordinal);
        Assert.Contains("KnownVersionSource=WorkspaceRepositoryLink.GitVersion", text, StringComparison.Ordinal);
        Assert.DoesNotContain(text, "Push dependency wait timed out", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transient_registry_failure_is_not_package_absence_and_the_wait_can_still_succeed()
    {
        var nuget = new ScriptedNuGetHandler { PackageFailuresRemaining = 2, FailureStatus = HttpStatusCode.ServiceUnavailable };
        nuget.VersionsByPackage["found.a"] = ["1.2.3"];
        var logs = new CapturingLoggerProvider();
        await using var ctx = await CreateContextAsync(nuget, logs);
        await SeedGraphAsync(ctx,
            [new GraphRepo("producer-a", 1, "Found.A", "1.2.3"), new GraphRepo("consumer-one", 2, null, "2.0.0")],
            [new GraphEdge("consumer-one", "Found.A", "1.2.3")]);

        var errors = await RunPushAsync(ctx, "transient");

        Assert.Empty(errors);
        var text = logs.Text;
        Assert.Contains("LastLookupResult=RegistryFailure", text, StringComparison.Ordinal);
        Assert.Contains("LastLookupResult=Available", text, StringComparison.Ordinal);
        Assert.DoesNotContain(text, "Push dependency wait timed out", StringComparison.Ordinal);
        Assert.DoesNotContain(SecretToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", text, StringComparison.Ordinal);
        var registryLines = logs.Lines.Count(line => line.Contains("LastLookupResult=RegistryFailure", StringComparison.Ordinal));
        Assert.Equal(1, registryLines);
    }

    [Fact]
    public async Task Partial_push_summary_names_completed_blocked_and_unprocessed_levels()
    {
        var nuget = new ScriptedNuGetHandler();
        nuget.VersionsByPackage["level.one"] = ["1.0.0"];
        var logs = new CapturingLoggerProvider();
        await using var ctx = await CreateContextAsync(nuget, logs);
        var repos = new List<GraphRepo> { new("level-1", 1, "Level.One", "1.0.0"), new("level-2", 2, "Level.Two", "1.0.0") };
        var edges = new List<GraphEdge> { new("level-2", "Level.One", "1.0.0"), new("level-3", "Level.Two", "1.0.0") };
        repos.Add(new GraphRepo("level-3", 3, "Level.Three", "1.0.0"));
        for (var level = 4; level <= 8; level++)
            repos.Add(new GraphRepo($"level-{level}", level, null, "1.0.0"));
        await SeedGraphAsync(ctx, repos, edges);

        var errors = await RunPushAsync(ctx, "partial");

        var timeout = Assert.Single(errors);
        Assert.Equal(3, timeout.Level);
        var pushed = ctx.WorkerBridge.Calls
            .Where(call => call.Command == "PushRepository")
            .Select(call => Args(call.Args).GetProperty("repositoryName").GetString())
            .ToList();
        Assert.Equal(new[] { "level-1", "level-2" }, pushed);
        var text = logs.Text;
        Assert.Contains("CompletedLevels=1,2", text, StringComparison.Ordinal);
        Assert.Contains("FailedLevel=3", text, StringComparison.Ordinal);
        Assert.Contains("UnprocessedLevels=4,5,6,7,8", text, StringComparison.Ordinal);
        Assert.Contains("BlockingPackages=Level.Two@1.0.0", text, StringComparison.Ordinal);
        Assert.Contains("TotalLevels=8", text, StringComparison.Ordinal);
        Assert.Contains("PushSucceeded=False", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_later_invocation_logs_the_state_the_existing_push_selection_uses()
    {
        var nuget = new ScriptedNuGetHandler();
        var logs = new CapturingLoggerProvider();
        await using var ctx = await CreateContextAsync(nuget, logs);
        var graph = await SeedGraphAsync(ctx,
            [new GraphRepo("level-1", 1, "Level.One", "1.0.0"), new GraphRepo("level-2", 2, null, "1.0.0")],
            [new GraphEdge("level-2", "Level.One", "1.0.0")]);

        var firstErrors = await RunPushAsync(ctx, "first");
        Assert.Single(firstErrors);
        Assert.Single(ctx.WorkerBridge.Calls, call => call.Command == "PushRepository");

        nuget.VersionsByPackage["level.one"] = ["1.0.0"];
        var secondErrors = await RunPushAsync(ctx, "second");
        Assert.Empty(secondErrors);

        var secondState = logs.Lines.Last(line =>
            line.Contains("[PushOrchestrator second]", StringComparison.Ordinal)
            && line.Contains("Push invocation state", StringComparison.Ordinal));
        Assert.Contains("Algorithm=PushEverySelectedRepository", secondState, StringComparison.Ordinal);
        // The first push's commit-count write and dependency recompute leave context rows whose outgoing count
        // is not positive, so the existing needing-push check treats both repositories as complete. The link for
        // the repository that was never pushed still has local commits, and this algorithm pushes every selected
        // repository anyway.
        var completeIds = new[] { graph.Ids["level-1"], graph.Ids["level-2"] }.OrderBy(id => id);
        Assert.Contains("NeedingPush=[none]", secondState, StringComparison.Ordinal);
        Assert.Contains($"ConsideredCompleteByNeedingPushCheck=[{string.Join(",", completeIds)}]", secondState, StringComparison.Ordinal);
        Assert.Contains($"LocalCommitsPending=[{graph.Ids["level-2"]}]", secondState, StringComparison.Ordinal);
        var secondPushes = ctx.WorkerBridge.Calls
            .Where(call => call.Command == "PushRepository")
            .Select(call => Args(call.Args).GetProperty("repositoryName").GetString())
            .ToList();
        Assert.Equal(new[] { "level-1", "level-1", "level-2" }, secondPushes);
    }

    private static async Task<List<(int Level, string Message)>> RunPushAsync(SyncStateTestContext ctx, string runId)
    {
        ctx.WorkerBridge.Respond("PushRepository", new { success = true });
        ctx.WorkerBridge.Respond("GetCommitCounts", new { outgoingCommits = 0, incomingCommits = 0, hasUpstream = true });
        ctx.WorkerBridge.Respond("DotnetRestore", new { success = true });
        ctx.WorkerBridge.Respond("EnsureWorkspace", new { success = true });

        var errors = new List<(int Level, string Message)>();
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspacePushService>().RunPushAsync(
            ctx.WorkspaceId,
            special,
            onLevelError: (level, message) => errors.Add((level, message)),
            packageRegistriesAlreadySynced: true,
            cancellationToken: CancellationToken.None,
            runId: runId,
            restorePackages: false);
        return errors;
    }

    private static Task<SyncStateTestContext> CreateContextAsync(ScriptedNuGetHandler nuget, CapturingLoggerProvider logs)
        => SyncStateTestContext.CreateAsync(configureServices: services =>
        {
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Debug);
                builder.AddFilter("GrayMoon", LogLevel.Debug);
            });
            services.AddSingleton(logs);
            services.AddSingleton<ILoggerProvider>(logs);
            services.Configure<WorkspaceOptions>(options => options.PushWaitDependencyTimeoutMinutesPerDependency = 0.1);
            services.AddSingleton(new HttpClient(nuget));
            services.AddScoped<NuGetService>();
            services.AddScoped<PackageRegistrySyncService>();
            services.AddScoped<WorkspacePushService>();
        });

    private static async Task<SeededGraph> SeedGraphAsync(
        SyncStateTestContext ctx,
        IReadOnlyList<GraphRepo> repos,
        IReadOnlyList<GraphEdge> edges)
    {
        var special = await ctx.GetSpecialContextIdAsync();
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        var seeded = await db.WorkspaceRepositories.FirstAsync(link => link.RepositoryId == ctx.RepositoryId);
        seeded.CheckedOutTag = "pinned-out";
        seeded.OutgoingCommits = 0;

        var gitHubConnectorId = await db.Repositories.Where(repo => repo.RepositoryId == ctx.RepositoryId).Select(repo => repo.ConnectorId).FirstAsync();
        var nuget = new Connector
        {
            ConnectorName = "nuget-test",
            ConnectorType = ConnectorType.NuGet,
            ApiBaseUrl = NuGetIndexUrl,
            IsActive = true,
            IsHealthy = true,
            UserToken = SecretToken
        };
        db.Connectors.Add(nuget);
        await db.SaveChangesAsync();

        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in repos)
        {
            var repository = new Repository
            {
                ConnectorId = gitHubConnectorId,
                RepositoryName = repo.Name,
                OrgName = "acme",
                Visibility = "Public",
                CloneUrl = $"https://github.com/acme/{repo.Name}.git"
            };
            db.Repositories.Add(repository);
            await db.SaveChangesAsync();
            db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
            {
                WorkspaceId = ctx.WorkspaceId,
                RepositoryId = repository.RepositoryId,
                GitVersion = repo.GitVersion,
                BranchName = "feature/x",
                DefaultBranchName = "main",
                OutgoingCommits = repo.Outgoing,
                BranchHasUpstream = true,
                DependencyLevel = repo.Level,
                SyncStatus = RepoSyncStatus.InSync
            });
            if (!string.IsNullOrWhiteSpace(repo.PackageId))
            {
                db.WorkspaceProjects.Add(new WorkspaceProject
                {
                    WorkspaceId = ctx.WorkspaceId,
                    WorkspaceFeatureContextId = special.Value,
                    RepositoryId = repository.RepositoryId,
                    ProjectName = repo.PackageId,
                    PackageId = repo.PackageId,
                    ProjectType = ProjectType.Library,
                    ProjectFilePath = $"src/{repo.Name}/{repo.PackageId}.csproj",
                    TargetFramework = "net10.0",
                    MatchedConnectorId = nuget.ConnectorId
                });
            }
            else
            {
                db.WorkspaceProjects.Add(new WorkspaceProject
                {
                    WorkspaceId = ctx.WorkspaceId,
                    WorkspaceFeatureContextId = special.Value,
                    RepositoryId = repository.RepositoryId,
                    ProjectName = repo.Name,
                    ProjectType = ProjectType.Library,
                    ProjectFilePath = $"src/{repo.Name}/{repo.Name}.csproj",
                    TargetFramework = "net10.0"
                });
            }

            ids[repo.Name] = repository.RepositoryId;
        }

        await db.SaveChangesAsync();
        var projects = await db.WorkspaceProjects.Where(project => project.WorkspaceId == ctx.WorkspaceId).ToListAsync();
        var projectByRepo = projects.ToDictionary(project => project.RepositoryId);
        foreach (var edge in edges)
        {
            var consumerId = ids[edge.Consumer];
            var producer = projects.First(project => string.Equals(project.PackageId, edge.PackageId, StringComparison.OrdinalIgnoreCase));
            db.ProjectDependencies.Add(new ProjectDependency
            {
                DependentProjectId = projectByRepo[consumerId].ProjectId,
                ReferencedProjectId = producer.ProjectId,
                Version = edge.Version
            });
        }

        db.WorkspaceRepositoryContextStates.RemoveRange(db.WorkspaceRepositoryContextStates);
        await db.SaveChangesAsync();
        return new SeededGraph(ids, nuget.ConnectorId);
    }

    private static Connector Connector()
        => new()
        {
            ConnectorName = "nuget-test",
            ConnectorType = ConnectorType.NuGet,
            ApiBaseUrl = NuGetIndexUrl,
            IsActive = true,
            IsHealthy = true,
            UserToken = SecretToken
        };

    private static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args);

    private sealed record GraphRepo(string Name, int Level, string? PackageId, string GitVersion, int Outgoing = 1);
    private sealed record GraphEdge(string Consumer, string PackageId, string Version);
    private sealed record SeededGraph(Dictionary<string, int> Ids, int ConnectorId);

    private sealed class ScriptedNuGetHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _urls = new();
        private int _packageFailuresRemaining;

        public Dictionary<string, string[]> VersionsByPackage { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HttpStatusCode? PackageStatus { get; set; }
        public HttpStatusCode FailureStatus { get; init; } = HttpStatusCode.ServiceUnavailable;
        public string? PackageBody { get; set; }
        public int PackageFailuresRemaining { get => _packageFailuresRemaining; set => _packageFailuresRemaining = value; }
        public IReadOnlyList<string> Urls => _urls.ToList();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            _urls.Enqueue(url);
            if (url.Equals(NuGetIndexUrl, StringComparison.OrdinalIgnoreCase))
                return Json("""{"resources":[{"@type":"PackageBaseAddress/3.0.0","@id":"https://nuget.test/flat/"}]}""");

            if (url.StartsWith("https://nuget.test/flat/", StringComparison.OrdinalIgnoreCase))
            {
                if (PackageStatus is HttpStatusCode status && status != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(status));
                if (Interlocked.Decrement(ref _packageFailuresRemaining) >= 0)
                    return Task.FromResult(new HttpResponseMessage(FailureStatus));
                if (PackageBody != null)
                    return Json(PackageBody);

                var id = url["https://nuget.test/flat/".Length..].Split('/')[0];
                if (VersionsByPackage.TryGetValue(id, out var versions))
                    return Json(JsonSerializer.Serialize(new { versions }));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => _lines.ToList();
    public string Text => string.Join('\n', _lines);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _lines);
    public void Dispose() { }

    private sealed class CapturingLogger(string categoryName, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lines.Enqueue($"{logLevel}: {categoryName}: {formatter(state, exception)}");
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
