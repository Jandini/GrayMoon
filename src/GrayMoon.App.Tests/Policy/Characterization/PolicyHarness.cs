using System.Net;
using System.Text.Json;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Queries;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests.Policy.Characterization;

/// <summary>
/// Shared setup for the Phase 0A characterization tests of the read-only repositories plan. These tests record what
/// the code does today with a tag-pinned repository, before any policy extraction; they are not a statement of what
/// it should do. A test named <c>..._has_no_service_side_tag_check_today</c> records a missing check.
/// </summary>
internal static class PolicyHarness
{
    public const string ProducerName = "graymoon-api";
    public const string ConsumerName = "graymoon-web";
    public const string PackageId = "Acme.Api";
    public const string Tag = "v1.0.0";

    /// <summary>The real container with every facade the characterization tests call. Nothing reaches the network.</summary>
    public static Task<SyncStateTestContext> CreateAsync(Action<IServiceCollection>? configure = null)
        => SyncStateTestContext.CreateAsync(configureServices: services =>
        {
            services.AddSingleton(new HttpClient(new OfflineHandler()));
            services.AddScoped<NuGetService>();
            services.AddScoped<PackageRegistrySyncService>();
            services.AddScoped<WorkspacePushService>();
            services.AddScoped<IPushLane>(sp => sp.GetRequiredService<WorkspacePushService>());
            services.AddScoped<PushOrchestrator>();
            services.AddScoped<WorkspacePushHandler>();
            services.AddScoped<BasicGitPushStrategy>();
            services.AddScoped<DotNetDependencyPushStrategy>();
            services.AddScoped<WorkspacePushStrategySelector>();
            services.AddScoped<IWorkspacePushOperations, WorkspacePushOperations>();
            services.AddSingleton<WorkspaceOperationOutcomeStore>();
            services.AddSingleton<UpdateAndPushApprovalStore>();
            services.AddScoped<WorkspaceUndoPushHandler>();
            services.AddSingleton<WorkerSyncNotificationQueue>();
            services.AddSingleton<IWorkerSyncNotificationBarrier>(sp => sp.GetRequiredService<WorkerSyncNotificationQueue>());
            services.AddScoped<DependencyUpdateOrchestrator>();
            services.AddScoped<IDependencyUpdateLane>(sp => sp.GetRequiredService<DependencyUpdateOrchestrator>());
            services.AddScoped<UpdateAndPushOrchestrator>();
            services.AddScoped<WorkspaceUpdateHandler>();
            services.AddScoped<IWorkspaceUpdateOperations, WorkspaceUpdateOperations>();
            services.AddScoped<IWorkspaceUpdateAndPushOperations, WorkspaceUpdateAndPushOperations>();
            services.AddScoped<IWorkspaceSyncOperations, WorkspaceSyncOperations>();
            services.AddScoped<IWorkspaceBranchOccupancyService, WorkspaceBranchOccupancyService>();
            services.AddScoped<WorkspaceBranchHandler>();
            services.AddScoped<IPrepareWorkspacePhases, PrepareWorkspacePhases>();
            services.AddScoped<PrepareWorkspaceOrchestrator>();
            services.AddScoped<IWorkspacePreparationOperations, WorkspacePreparationOperations>();
            services.AddScoped<IGitChangesWorkerClient, GitChangesWorkerClient>();
            // The read service is only used by GetAsync, which these tests do not call.
            services.AddScoped<IWorkspaceGitChangesReadService>(_ => null!);
            services.AddScoped<IWorkspaceGitChangesOperations, WorkspaceGitChangesOperations>();
            configure?.Invoke(services);
        });

    public sealed record Graph(int ProducerId, int ConsumerId);

    /// <summary>One repository of a seeded Feature. <see cref="WithState"/> false leaves the Feature without a state row for it.</summary>
    public sealed record FeatureRepo(
        int RepositoryId,
        string? Branch = "feature/y",
        string? Tag = null,
        bool WithState = true,
        int Outgoing = 3,
        bool? HasUpstream = true,
        string? PinnedTag = null);

    /// <summary>
    /// Producer (the seeded repository, level 1) publishes <see cref="PackageId"/>; a second repository (level 2)
    /// consumes it at a stale version, so the consumer is the repository an update would change. Both are on
    /// feature/x with unpushed commits. The special Workspace reads the links (its state rows are dropped).
    /// </summary>
    public static async Task<Graph> SeedGraphAsync(SyncStateTestContext ctx)
    {
        var special = await ctx.GetSpecialContextIdAsync();
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();

        var producerLink = await db.WorkspaceRepositories.Include(l => l.Repository).FirstAsync(l => l.RepositoryId == ctx.RepositoryId);
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
        });
        await db.SaveChangesAsync();
        // InSync is the insert sentinel of this column (an inserted InSync is stored as NeedsSync), so it is set by update.
        var consumerLink = await db.WorkspaceRepositories.FirstAsync(l => l.RepositoryId == consumer.RepositoryId);
        consumerLink.SyncStatus = RepoSyncStatus.InSync;
        await db.SaveChangesAsync();

        await AddProjectsAsync(db, ctx.WorkspaceId, special, ctx.RepositoryId, consumer.RepositoryId);

        db.WorkspaceRepositoryContextStates.RemoveRange(db.WorkspaceRepositoryContextStates);
        await db.SaveChangesAsync();
        return new Graph(ctx.RepositoryId, consumer.RepositoryId);
    }

    private static async Task AddProjectsAsync(AppDbContext db, int workspaceId, WorkspaceFeatureContextId contextId, int producerId, int consumerId)
    {
        var producerProject = new WorkspaceProject
        {
            WorkspaceId = workspaceId,
            WorkspaceFeatureContextId = contextId.Value,
            RepositoryId = producerId,
            ProjectName = "Acme.Api",
            PackageId = PackageId,
            ProjectType = ProjectType.Library,
            ProjectFilePath = "src/Api/Acme.Api.csproj",
            TargetFramework = "net10.0",
        };
        var consumerProject = new WorkspaceProject
        {
            WorkspaceId = workspaceId,
            WorkspaceFeatureContextId = contextId.Value,
            RepositoryId = consumerId,
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
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A Feature context holding the given repositories. With <paramref name="copyGraphProjects"/> the Feature gets
    /// its own copy of the producer and consumer projects, so its update plan has something to report.
    /// </summary>
    public static async Task<WorkspaceFeatureContextId> SeedFeatureAsync(
        SyncStateTestContext ctx,
        IReadOnlyList<FeatureRepo> repos,
        Graph? copyGraphProjects = null)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = "feature-y",
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = ctx.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true,
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();

        foreach (var repo in repos)
        {
            var link = await db.WorkspaceRepositories.Include(l => l.Repository).SingleAsync(l => l.RepositoryId == repo.RepositoryId);
            db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                WorktreePath = $@"C:\Users\test\.graymoon\test-ws\features\feature-y\{link.Repository!.RepositoryName}",
                State = WorkspaceFeatureRepositoryState.Ready,
                BaseCommitSha = "abc123",
                ParentBranchName = "main",
                PinnedTag = repo.PinnedTag,
                CreatedAt = DateTime.UtcNow,
            });
            if (!repo.WithState)
                continue;

            db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                BranchName = repo.Tag == null ? repo.Branch : null,
                CheckedOutTag = repo.Tag,
                GitVersion = link.GitVersion,
                DependencyLevel = link.DependencyLevel,
                OutgoingCommits = repo.Outgoing,
                BranchHasUpstream = repo.HasUpstream,
                SyncStatus = RepoSyncStatus.InSync,
            });
        }

        await db.SaveChangesAsync();
        var contextId = new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
        if (copyGraphProjects != null)
            await AddProjectsAsync(db, ctx.WorkspaceId, contextId, copyGraphProjects.ProducerId, copyGraphProjects.ConsumerId);
        return contextId;
    }

    /// <summary>Puts the Workspace checkout of a repository on a tag, the way a tag checkout leaves the shared link.</summary>
    public static async Task PinWorkspaceAsync(SyncStateTestContext ctx, int repositoryId, string tag = Tag)
    {
        var special = await ctx.GetSpecialContextIdAsync();
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var link = await db.WorkspaceRepositories.SingleAsync(l => l.WorkspaceId == ctx.WorkspaceId && l.RepositoryId == repositoryId);
        link.CheckedOutTag = tag;
        link.BranchName = null;

        // Production mirrors the special Workspace's checkout onto both rows; a pin on the link alone is not a real state.
        var state = await db.WorkspaceRepositoryContextStates.SingleOrDefaultAsync(s =>
            s.WorkspaceFeatureContextId == special.Value && s.WorkspaceRepositoryId == link.WorkspaceRepositoryId);
        if (state != null)
        {
            state.CheckedOutTag = tag;
            state.BranchName = null;
        }

        await db.SaveChangesAsync();
    }

    public static async Task MutateLinkAsync(SyncStateTestContext ctx, int repositoryId, Action<WorkspaceRepositoryLink> mutate)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var link = await db.WorkspaceRepositories.SingleAsync(l => l.WorkspaceId == ctx.WorkspaceId && l.RepositoryId == repositoryId);
        mutate(link);
        await db.SaveChangesAsync();
    }

    public static async Task<WorkspaceRepositoryLink> ReadLinkAsync(SyncStateTestContext ctx, int repositoryId)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.WorkspaceRepositories.AsNoTracking()
            .SingleAsync(l => l.WorkspaceId == ctx.WorkspaceId && l.RepositoryId == repositoryId);
    }

    /// <summary>Answers every push-adjacent Worker command with success, so a push that is attempted completes.</summary>
    public static void RespondPushCommands(SyncStateTestContext ctx)
    {
        ctx.WorkerBridge.Respond("PushRepository", new { success = true });
        ctx.WorkerBridge.Respond("GetCommitCounts", new { outgoingCommits = 0, incomingCommits = 0, hasUpstream = true });
        ctx.WorkerBridge.Respond("DotnetRestore", new { success = true });
        ctx.WorkerBridge.Respond("EnsureWorkspace", new { success = true });
    }

    /// <summary>The arguments of every call of one Worker command, in the order they were sent.</summary>
    public static List<JsonElement> Calls(SyncStateTestContext ctx, string command)
    {
        lock (ctx.WorkerBridge.Calls)
        {
            return ctx.WorkerBridge.Calls.ToList()
                .Where(c => string.Equals(c.Command, command, StringComparison.OrdinalIgnoreCase))
                .Select(c => JsonSerializer.SerializeToElement(c.Args))
                .ToList();
        }
    }

    /// <summary>The repository names one Worker command was sent for.</summary>
    public static List<string?> RepositoryNames(SyncStateTestContext ctx, string command)
        => Calls(ctx, command)
            .Select(a => a.TryGetProperty("repositoryName", out var name) ? name.GetString() : null)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    public static WorkspaceRepositoryLinkListItemDto Dto(
        int repositoryId,
        string name,
        string? branch = "feature/x",
        string? defaultBranch = "main",
        string? tag = null,
        int outgoing = 0,
        int incoming = 0,
        bool? hasUpstream = true,
        int unmatchedDeps = 0,
        int outOfDateFiles = 0,
        int level = 0,
        WorkspaceRepositoryRole role = WorkspaceRepositoryRole.Source) => new(
        WorkspaceRepositoryId: 100 + repositoryId,
        WorkspaceId: 1,
        RepositoryId: repositoryId,
        RepositoryName: name,
        CloneUrl: $"https://example/{name}.git",
        GitVersion: "1.0.0",
        BranchName: tag == null ? branch : null,
        CheckedOutTag: tag,
        DefaultBranchName: defaultBranch,
        OutgoingCommits: outgoing,
        IncomingCommits: incoming,
        DefaultBranchBehindCommits: 0,
        DefaultBranchAheadCommits: 0,
        BranchHasUpstream: hasUpstream,
        SyncStatus: RepoSyncStatus.InSync,
        DependencyLevel: level,
        Dependencies: 0,
        UnmatchedDeps: unmatchedDeps,
        OutOfDateFileRepos: outOfDateFiles,
        RepositoryType: ProjectType.Library,
        HasNewerTag: null,
        HasSelfFileVersionToken: null,
        PullRequestState: null,
        PullRequestNumber: null,
        PullRequestHtmlUrl: null,
        PullRequestMergedAt: null,
        PullRequestMergeable: null,
        PullRequestMergeableState: null,
        PullRequestChangedFiles: null,
        Archived: false,
        UncommittedChangedFileCount: 0)
    {
        Role = role,
    };

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
