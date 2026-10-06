using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services.Application;
using GrayMoon.App.Services.Connectors;
using GrayMoon.App.Services.Git;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application;
using GrayMoon.Application.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Unit D: push, update and restore pick their behaviour from the workspace's capabilities. A Basic
/// workspace pushes plain git (no dependency query, no registry wait, no level order, no restore) and never
/// rewrites project files; a .NET Dependency workspace keeps the synchronized, level-ordered push. Every
/// remaining push-adjacent worker command carries the workspace's capabilities.
/// </summary>
public sealed class WorkspacePushStrategyTests
{
    private const string ConsumerName = "graymoon-web";
    private const string PackageId = "Acme.Api";
    private const string NuGetIndexUrl = "https://nuget.test/v3/index.json";

    [Fact]
    public async Task Selector_maps_capabilities_to_the_push_strategy()
    {
        await using var ctx = await CreateContextAsync(new RecordingNuGetHandler());
        await using var scope = ctx.CreateScope();
        var selector = scope.ServiceProvider.GetRequiredService<WorkspacePushStrategySelector>();
        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceCapabilitiesResolver>();

        Assert.IsType<BasicGitPushStrategy>(selector.Select(await resolver.GetAsync(ctx.WorkspaceId)));

        await ctx.UseDotNetDependencyProfileAsync();
        Assert.IsType<DotNetDependencyPushStrategy>(selector.Select(await resolver.GetAsync(ctx.WorkspaceId)));
    }

    [Fact]
    public async Task Basic_plan_lists_every_unpushed_repository_without_required_packages()
    {
        var nuget = new RecordingNuGetHandler();
        await using var ctx = await CreateContextAsync(nuget);
        var graph = await SeedDependencyGraphAsync(ctx, matchConnector: false);

        var plan = await GetPlanAsync(ctx);

        Assert.True(plan.HasUnpushed);
        Assert.Equal(new HashSet<int> { graph.ProducerId, graph.ConsumerId }, plan.RepositoryIds.ToHashSet());
        Assert.Empty(plan.RequiredPackageIds);
        Assert.Empty(nuget.Requests);
    }

    [Fact]
    public async Task Basic_push_runs_plain_git_push_without_registry_wait_level_order_or_restore()
    {
        var nuget = new RecordingNuGetHandler();
        await using var ctx = await CreateContextAsync(nuget);
        var graph = await SeedDependencyGraphAsync(ctx, matchConnector: false);
        RespondPushCommands(ctx);

        // A .NET workspace would refuse this push: the consumer's required package has no registry match. The synced
        // consumer would also be restored there.
        var result = await PushAsync(
            ctx,
            new HashSet<int> { graph.ProducerId, graph.ConsumerId },
            synchronizedPush: true,
            requiredPackageIds: new HashSet<string> { PackageId },
            syncedRepoIds: new HashSet<int> { graph.ConsumerId });

        Assert.True(result.Success);
        var pushes = PushCalls(ctx);
        Assert.Equal(new HashSet<int> { graph.ProducerId, graph.ConsumerId }, pushes.Select(p => p.GetProperty("repositoryId").GetInt32()).ToHashSet());
        Assert.All(pushes, p =>
        {
            Assert.False(p.GetProperty("refreshVersionAfterPush").GetBoolean());
            AssertCapabilities(p, calculateVersion: false, discoverProjects: false);
        });
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == "DotnetRestore");
        Assert.Empty(nuget.Requests);
    }

    [Fact]
    public async Task DotNet_plan_requires_the_producer_package()
    {
        await using var ctx = await CreateContextAsync(new RecordingNuGetHandler());
        await ctx.UseDotNetDependencyProfileAsync();
        var graph = await SeedDependencyGraphAsync(ctx, matchConnector: false);

        var plan = await GetPlanAsync(ctx);

        Assert.Equal(new HashSet<int> { graph.ProducerId, graph.ConsumerId }, plan.RepositoryIds.ToHashSet());
        Assert.Equal(new[] { PackageId }, plan.RequiredPackageIds.ToList());
    }

    [Fact]
    public async Task DotNet_synchronized_push_orders_levels_waits_for_the_package_and_restores_the_consumer()
    {
        var nuget = new RecordingNuGetHandler { PublishedVersions = ["0.9.0"] };
        await using var ctx = await CreateContextAsync(nuget);
        await ctx.UseDotNetDependencyProfileAsync();
        var graph = await SeedDependencyGraphAsync(ctx, matchConnector: false);
        RespondPushCommands(ctx);
        var plan = await GetPlanAsync(ctx);

        var result = await PushAsync(ctx, plan.RepositoryIds, synchronizedPush: true, plan.RequiredPackageIds, syncedRepoIds: new HashSet<int> { graph.ConsumerId });

        Assert.True(result.Success, result.Error);
        var pushes = PushCalls(ctx);
        Assert.Equal(new[] { graph.ProducerId, graph.ConsumerId }, pushes.Select(p => p.GetProperty("repositoryId").GetInt32()).ToList());
        Assert.All(pushes, p =>
        {
            Assert.True(p.GetProperty("refreshVersionAfterPush").GetBoolean());
            AssertCapabilities(p, calculateVersion: true, discoverProjects: true);
        });
        Assert.Contains(nuget.Requests, url => url.Contains("/acme.api/index.json", StringComparison.Ordinal));
        var restore = Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == "DotnetRestore");
        Assert.Equal(ConsumerName, Args(restore.Args).GetProperty("repositoryName").GetString());
    }

    [Fact]
    public async Task DotNet_synchronized_push_is_refused_when_a_required_package_has_no_registry()
    {
        await using var ctx = await CreateContextAsync(new RecordingNuGetHandler());
        await ctx.UseDotNetDependencyProfileAsync();
        await SeedDependencyGraphAsync(ctx, matchConnector: false);
        RespondPushCommands(ctx);
        var plan = await GetPlanAsync(ctx);

        await Assert.ThrowsAsync<SynchronizedPushNotPossibleException>(
            () => PushAsync(ctx, plan.RepositoryIds, synchronizedPush: true, plan.RequiredPackageIds));
        Assert.Empty(PushCalls(ctx));
    }

    [Fact]
    public async Task Basic_update_rewrites_no_project_files()
    {
        await using var ctx = await CreateContextAsync(new RecordingNuGetHandler());
        await SeedDependencyGraphAsync(ctx, matchConnector: true);
        var special = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            var (payload, _) = await git.GetUpdatePlanAsync(ctx.WorkspaceId, special);
            Assert.Empty(payload);
            Assert.Empty(await git.SyncDependenciesAsync(ctx.WorkspaceId, special));
        }

        DependencyUpdateRunResult result;
        await using (var scope = ctx.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<DependencyUpdateOrchestrator>();
            result = await orchestrator.RunAsync(ctx.WorkspaceId, special, CancellationToken.None, progress: null, (_, _) => { }, (_, _) => { });
        }

        Assert.True(result.Success);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command is "SyncRepositoryDependencies" or "RefreshRepositoryProjects");
    }

    [Fact]
    public async Task DotNet_update_plan_still_lists_the_consumer_with_a_stale_reference()
    {
        await using var ctx = await CreateContextAsync(new RecordingNuGetHandler());
        await ctx.UseDotNetDependencyProfileAsync();
        var graph = await SeedDependencyGraphAsync(ctx, matchConnector: true);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var (payload, _) = await scope.ServiceProvider.GetRequiredService<WorkspaceGitService>().GetUpdatePlanAsync(ctx.WorkspaceId, special);

        var consumer = Assert.Single(payload);
        Assert.Equal(graph.ConsumerId, consumer.RepoId);
    }

    [Fact]
    public async Task Basic_restore_is_a_no_op()
    {
        await using var ctx = await CreateContextAsync(new RecordingNuGetHandler());
        await SeedDependencyGraphAsync(ctx, matchConnector: true);
        ctx.WorkerBridge.Respond("DotnetRestore", new { success = true });

        var (all, synced) = await RestoreAsync(ctx);

        Assert.Equal(0, all);
        Assert.Equal(0, synced);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == "DotnetRestore");
    }

    [Fact]
    public async Task DotNet_restore_still_restores_every_tracked_project()
    {
        await using var ctx = await CreateContextAsync(new RecordingNuGetHandler());
        await ctx.UseDotNetDependencyProfileAsync();
        await SeedDependencyGraphAsync(ctx, matchConnector: true);
        ctx.WorkerBridge.Respond("DotnetRestore", new { success = true });

        var (all, synced) = await RestoreAsync(ctx);

        Assert.Equal(2, all);
        Assert.Equal(2, synced);
        Assert.Equal(4, ctx.WorkerBridge.Calls.Count(c => c.Command == "DotnetRestore"));
    }

    [Fact]
    public async Task Undo_push_carries_the_workspace_capabilities()
    {
        await using var ctx = await CreateContextAsync(new RecordingNuGetHandler());
        ctx.WorkerBridge.Respond("UndoPush", new { success = true });
        var special = await ctx.GetSpecialContextIdAsync();
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        List<WorkspaceRepositoryLink> links;
        await using (var db = await factory.CreateDbContextAsync())
        {
            links = await db.WorkspaceRepositories.AsNoTracking()
                .Include(l => l.Repository).ThenInclude(r => r!.Connector)
                .Where(l => l.WorkspaceId == ctx.WorkspaceId)
                .ToListAsync();
        }

        await using var scope = ctx.CreateScope();
        var results = await scope.ServiceProvider.GetRequiredService<WorkspaceUndoPushHandler>()
            .RunUndoPushAsync(ctx.WorkspaceId, special, links, keepChanges: true, progress: null, CancellationToken.None);

        Assert.True(Assert.Single(results).Success);
        AssertCapabilities(Args(Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == "UndoPush").Args), calculateVersion: false, discoverProjects: false);
    }

    [Fact]
    public async Task Fetch_commits_carries_the_workspace_capabilities()
    {
        await using var ctx = await CreateContextAsync(new RecordingNuGetHandler());
        await ctx.UseDotNetDependencyProfileAsync();
        ctx.WorkerBridge.Respond("FetchCommits", new { success = true });
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspaceGitService>().QuickFetchAsync(ctx.WorkspaceId, special);

        AssertCapabilities(Args(Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == "FetchCommits").Args), calculateVersion: true, discoverProjects: true);
    }

    [Fact]
    public async Task Return_to_default_carries_the_workspace_and_its_capabilities()
    {
        await using var ctx = await CreateContextAsync(new RecordingNuGetHandler());
        ctx.WorkerBridge.Respond("ReturnToDefaultBranch", new ReturnToDefaultBranchResponse
        {
            Success = true,
            CurrentBranch = "main",
            DefaultBranch = "main",
            State = new RepositoryStateSnapshot { BranchName = "main", IdentityProbed = true },
        });
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var (success, error) = await scope.ServiceProvider.GetRequiredService<WorkspaceGitService>()
            .ReturnToDefaultDirectAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "feature/x", deleteRemoteBranch: false, allowForceDeleteLocalBranch: false, CancellationToken.None);

        Assert.True(success, error);
        var args = Args(Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == "ReturnToDefaultBranch").Args);
        Assert.Equal(ctx.WorkspaceId, args.GetProperty("workspaceId").GetInt32());
        AssertCapabilities(args, calculateVersion: false, discoverProjects: false);
    }

    [Fact]
    public async Task Git_change_status_carries_the_workspace_capabilities_and_none_for_a_missing_workspace()
    {
        await using var ctx = await CreateContextAsync(new RecordingNuGetHandler());
        ctx.WorkerBridge.Respond("GetGitChangeStatus", new { success = true });

        await using var scope = ctx.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IGitChangesWorkerClient>();
        await client.GetStatusAsync(@"C:\gm-test-root", "test-ws", "graymoon-api", null, ctx.WorkspaceId, ctx.RepositoryId, CancellationToken.None);
        await client.GetStatusAsync(@"C:\gm-test-root", "test-ws", "graymoon-api", null, workspaceId: 999_999, ctx.RepositoryId, CancellationToken.None);

        var calls = ctx.WorkerBridge.Calls.Where(c => c.Command == "GetGitChangeStatus").Select(c => Args(c.Args)).ToList();
        Assert.Equal(2, calls.Count);
        AssertCapabilities(calls[0], calculateVersion: false, discoverProjects: false);
        Assert.Equal(JsonValueKind.Null, calls[1].GetProperty("capabilities").ValueKind);
    }

    private static Task<SyncStateTestContext> CreateContextAsync(RecordingNuGetHandler nuget)
        => SyncStateTestContext.CreateAsync(configureServices: services =>
        {
            services.AddSingleton(new HttpClient(nuget));
            services.AddScoped<NuGetService>();
            services.AddScoped<PackageRegistrySyncService>();
            services.AddScoped<WorkspacePushService>();
            services.AddScoped<PushOrchestrator>();
            services.AddScoped<WorkspacePushHandler>();
            services.AddScoped<BasicGitPushStrategy>();
            services.AddScoped<DotNetDependencyPushStrategy>();
            services.AddScoped<WorkspacePushStrategySelector>();
            services.AddScoped<IWorkspacePushOperations, WorkspacePushOperations>();
            services.AddScoped<WorkspaceUndoPushHandler>();
            services.AddScoped<DependencyUpdateOrchestrator>();
            services.AddScoped<IGitChangesWorkerClient, GitChangesWorkerClient>();
        });

    private sealed record DependencyGraph(int ProducerId, int ConsumerId);

    /// <summary>
    /// Producer (the seeded repository, level 1) publishes <see cref="PackageId"/>; a second repository (level 2)
    /// consumes it at a stale version. Both have unpushed commits. A NuGet connector exists; the producer project
    /// is matched to it only when <paramref name="matchConnector"/> is set.
    /// </summary>
    private static async Task<DependencyGraph> SeedDependencyGraphAsync(SyncStateTestContext ctx, bool matchConnector)
    {
        var special = await ctx.GetSpecialContextIdAsync();
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        var producerLink = await db.WorkspaceRepositories.Include(l => l.Repository).FirstAsync(l => l.RepositoryId == ctx.RepositoryId);
        var nugetConnector = new Connector
        {
            ConnectorName = "nuget-test",
            ConnectorType = ConnectorType.NuGet,
            ApiBaseUrl = NuGetIndexUrl,
            IsActive = true,
            IsHealthy = true,
        };
        db.Connectors.Add(nugetConnector);
        var consumer = new Repository
        {
            ConnectorId = producerLink.Repository!.ConnectorId,
            RepositoryName = ConsumerName,
            OrgName = "acme",
            Visibility = "Public",
            CloneUrl = $"https://github.com/acme/{ConsumerName}.git",
        };
        db.Repositories.Add(consumer);
        await db.SaveChangesAsync();

        producerLink.DependencyLevel = 1;
        db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = consumer.RepositoryId,
            GitVersion = "2.0.0",
            BranchName = "feature/x",
            DefaultBranchName = "main",
            OutgoingCommits = 1,
            BranchHasUpstream = true,
            DependencyLevel = 2,
            SyncStatus = RepoSyncStatus.InSync,
        });

        var producerProject = new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId,
            WorkspaceFeatureContextId = special.Value,
            RepositoryId = ctx.RepositoryId,
            ProjectName = "Acme.Api",
            PackageId = PackageId,
            ProjectType = ProjectType.Library,
            ProjectFilePath = "src/Api/Acme.Api.csproj",
            TargetFramework = "net10.0",
            MatchedConnectorId = matchConnector ? nugetConnector.ConnectorId : null,
        };
        var consumerProject = new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId,
            WorkspaceFeatureContextId = special.Value,
            RepositoryId = consumer.RepositoryId,
            ProjectName = "Acme.Web",
            ProjectType = ProjectType.Library,
            ProjectFilePath = "src/Web/Acme.Web.csproj",
            TargetFramework = "net10.0",
        };
        db.WorkspaceProjects.AddRange(producerProject, consumerProject);
        await db.SaveChangesAsync();

        db.ProjectDependencies.Add(new ProjectDependency
        {
            DependentProjectId = consumerProject.ProjectId,
            ReferencedProjectId = producerProject.ProjectId,
            Version = "0.9.0",
        });

        // Levels and counts live on the links; drop the context rows so the special Workspace falls back to them.
        db.WorkspaceRepositoryContextStates.RemoveRange(db.WorkspaceRepositoryContextStates);
        await db.SaveChangesAsync();
        return new DependencyGraph(ctx.RepositoryId, consumer.RepositoryId);
    }

    private static void RespondPushCommands(SyncStateTestContext ctx)
    {
        ctx.WorkerBridge.Respond("PushRepository", new { success = true });
        ctx.WorkerBridge.Respond("GetCommitCounts", new { outgoingCommits = 0, incomingCommits = 0, hasUpstream = true });
        ctx.WorkerBridge.Respond("DotnetRestore", new { success = true });
        ctx.WorkerBridge.Respond("EnsureWorkspace", new { success = true });
    }

    private static async Task<WorkspacePushPlan> GetPlanAsync(SyncStateTestContext ctx)
    {
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>().GetPlanAsync(ctx.WorkspaceId, special);
    }

    private static async Task<OperationResult> PushAsync(
        SyncStateTestContext ctx,
        IReadOnlySet<int> repositoryIds,
        bool synchronizedPush,
        IReadOnlySet<string> requiredPackageIds,
        IReadOnlySet<int>? syncedRepoIds = null)
    {
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>()
            .PushAsync(ctx.WorkspaceId, special, repositoryIds, synchronizedPush, requiredPackageIds, syncedRepoIds: syncedRepoIds);
    }

    private static async Task<(int All, int Synced)> RestoreAsync(SyncStateTestContext ctx)
    {
        var special = await ctx.GetSpecialContextIdAsync();
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        HashSet<int> repoIds;
        await using (var db = await factory.CreateDbContextAsync())
            repoIds = (await db.WorkspaceRepositories.Where(l => l.WorkspaceId == ctx.WorkspaceId).Select(l => l.RepositoryId).ToListAsync()).ToHashSet();

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var all = await git.RestoreAllWorkspacePackagesAsync(ctx.WorkspaceId, special, _ => { }, CancellationToken.None);
        var synced = await git.RestoreSyncedWorkspacePackagesAsync(ctx.WorkspaceId, special, repoIds, _ => { }, CancellationToken.None);
        return (all, synced);
    }

    private static List<JsonElement> PushCalls(SyncStateTestContext ctx)
        => ctx.WorkerBridge.Calls.Where(c => c.Command == "PushRepository").Select(c => Args(c.Args)).ToList();

    private static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args);

    private static void AssertCapabilities(JsonElement args, bool calculateVersion, bool discoverProjects)
    {
        var capabilities = args.GetProperty("capabilities");
        Assert.Equal(calculateVersion, capabilities.GetProperty("calculateRepositoryVersion").GetBoolean());
        Assert.Equal(discoverProjects, capabilities.GetProperty("discoverDotNetProjects").GetBoolean());
    }

    /// <summary>
    /// A NuGet v3 feed at <see cref="NuGetIndexUrl"/> that records every request. It lists
    /// <see cref="PublishedVersions"/> for every package; with none published, every package lookup is a 404.
    /// </summary>
    private sealed class RecordingNuGetHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _requests = new();

        public IReadOnlyList<string> PublishedVersions { get; init; } = [];

        public IReadOnlyList<string> Requests => _requests.ToList();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            _requests.Enqueue(url);
            if (url.Equals(NuGetIndexUrl, StringComparison.OrdinalIgnoreCase))
                return Json("""{"resources":[{"@type":"PackageBaseAddress/3.0.0","@id":"https://nuget.test/flat/"}]}""");
            if (url.StartsWith("https://nuget.test/flat/", StringComparison.OrdinalIgnoreCase) && PublishedVersions.Count > 0)
                return Json(JsonSerializer.Serialize(new { versions = PublishedVersions }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
