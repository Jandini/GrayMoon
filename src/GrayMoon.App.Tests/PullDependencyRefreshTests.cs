using System.Text.Json;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// A pull runs with hooks off, so no merge hook reports what it brought in. The pull used to store the branch,
/// the version and the counts only, and recompute nothing: the dependency badges kept their pre-pull values until
/// the next Sync. The Worker now returns the projects with the pull result and each pull job ends with one recompute.
/// </summary>
public sealed class PullDependencyRefreshTests
{
    private const string ProducerName = "graymoon-api";
    private const string ConsumerName = "graymoon-web";
    private const string PackageName = "Acme.Api";

    private static object[] ProducerProjects() =>
    [
        new
        {
            name = PackageName,
            projectType = 3,
            projectPath = "src/Acme.Api/Acme.Api.csproj",
            targetFramework = "net10.0",
            packageId = PackageName,
            packageReferences = Array.Empty<object>(),
        },
    ];

    private static object[] ConsumerProjects(string referencedVersion) =>
    [
        new
        {
            name = "Acme.Web",
            projectType = 2,
            projectPath = "src/Acme.Web/Acme.Web.csproj",
            targetFramework = "net10.0",
            packageId = (string?)null,
            packageReferences = new object[] { new { name = PackageName, version = referencedVersion } },
        },
    ];

    private static object PulledResponse(string version, object[]? projects) => new
    {
        success = true,
        mergeConflict = false,
        version,
        branch = "main",
        state = new
        {
            branchName = "main",
            gitVersion = version,
            defaultBranchName = "main",
            outgoingCommits = 0,
            incomingCommits = 0,
            defaultBranchBehind = 0,
            defaultBranchAhead = 0,
            hasUpstream = true,
            projects,
            identityProbed = true,
            gitVersionProbed = true,
            commitCountsProbed = true,
            upstreamProbed = true,
            projectsProbed = projects != null,
        },
    };

    [Fact]
    public async Task Pulling_a_new_version_of_a_package_marks_its_consumer_without_a_Sync()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var consumerId = await SeedGraphAsync(ctx, producerVersion: "1.0.0", consumerReferences: "1.0.0");
        Assert.Equal(0, (await ReadLinkAsync(ctx, consumerId)).UnmatchedDeps);
        ctx.WorkerBridge.Respond("CommitSyncRepository", PulledResponse("2.0.0", ProducerProjects()));

        await PullAsync(ctx, ctx.RepositoryId);

        Assert.Equal("2.0.0", (await ctx.ReadLinkAsync()).GitVersion);
        Assert.Equal(1, (await ReadLinkAsync(ctx, consumerId)).UnmatchedDeps);
        Assert.Contains(ctx.Broadcasts, b => b.Method == "ContextSynced");
    }

    [Fact]
    public async Task Pulling_an_updated_reference_clears_the_badge_without_a_Sync()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var consumerId = await SeedGraphAsync(ctx, producerVersion: "2.0.0", consumerReferences: "1.0.0");
        Assert.Equal(1, (await ReadLinkAsync(ctx, consumerId)).UnmatchedDeps);
        // Someone updated the reference on another machine and pushed; the pull brings the changed project file.
        ctx.WorkerBridge.Respond("CommitSyncRepository", PulledResponse("1.0.1", ConsumerProjects("2.0.0")));

        await PullAsync(ctx, consumerId);

        Assert.Equal(0, (await ReadLinkAsync(ctx, consumerId)).UnmatchedDeps);
        var edge = Assert.Single(await ctx.ReadDependenciesAsync());
        Assert.Equal("2.0.0", edge.Version);
    }

    [Fact]
    public async Task A_bulk_pull_stores_every_repository_and_recomputes_once()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var consumerId = await SeedGraphAsync(ctx, producerVersion: "1.0.0", consumerReferences: "1.0.0");
        ctx.WorkerBridge.Respond("CommitSyncRepository", args =>
        {
            var name = JsonSerializer.SerializeToElement(args).GetProperty("repositoryName").GetString();
            return new WorkerCommandResponse(
                true,
                name == ProducerName
                    ? PulledResponse("2.0.0", ProducerProjects())
                    : PulledResponse("1.0.1", ConsumerProjects("1.5.0")),
                null);
        });
        var contextSyncedBefore = ctx.Broadcasts.Count(b => b.Method == "ContextSynced");

        await using (var scope = ctx.CreateScope())
        {
            var special = await ctx.GetSpecialContextIdAsync();
            await scope.ServiceProvider.GetRequiredService<WorkspaceCommitSyncHandler>().CommitSyncLevelAsync(
                ctx.WorkspaceId,
                special,
                [ctx.RepositoryId, consumerId],
                CancellationToken.None,
                (_, _) => Task.CompletedTask,
                (_, _) => { },
                _ => { });
        }

        // Producer is at 2.0.0 and the consumer now references 1.5.0: both pulls are in the one recompute.
        Assert.Equal(1, (await ReadLinkAsync(ctx, consumerId)).UnmatchedDeps);
        var edge = Assert.Single(await ctx.ReadDependenciesAsync());
        Assert.Equal("1.5.0", edge.Version);
        // One recompute for the two repositories: the version-file check announces itself, then the pull does.
        Assert.Equal(2, ctx.Broadcasts.Count(b => b.Method == "ContextSynced") - contextSyncedBefore);
    }

    [Fact]
    public async Task A_pull_that_reports_no_projects_keeps_the_stored_ones_and_still_recomputes()
    {
        // A failed project scan is "not probed": the pull must not read that as "no projects".
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var consumerId = await SeedGraphAsync(ctx, producerVersion: "1.0.0", consumerReferences: "1.0.0");
        ctx.WorkerBridge.Respond("CommitSyncRepository", PulledResponse("2.0.0", projects: null));

        await PullAsync(ctx, ctx.RepositoryId);

        Assert.Single(await ctx.ReadProjectsAsync());
        Assert.Single(await ctx.ReadDependenciesAsync());
        Assert.Equal(1, (await ReadLinkAsync(ctx, consumerId)).UnmatchedDeps);
    }

    [Fact]
    public async Task A_Basic_workspace_stores_no_projects_and_gains_no_dependency_state_from_a_pull()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("CommitSyncRepository", PulledResponse("-", ProducerProjects()));

        await PullAsync(ctx, ctx.RepositoryId);

        Assert.Empty(await ctx.ReadProjectsAsync());
        var link = await ctx.ReadLinkAsync();
        Assert.Null(link.DependencyLevel);
        Assert.Null(link.UnmatchedDeps);
    }

    [Fact]
    public async Task The_pull_request_carries_the_workspace_capabilities()
    {
        // Without them the Worker falls back to full enrichment and scans projects for a Basic workspace.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("CommitSyncRepository", PulledResponse("-", projects: null));

        await PullAsync(ctx, ctx.RepositoryId);

        var call = Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == "CommitSyncRepository");
        var capabilities = JsonSerializer.SerializeToElement(call.Args).GetProperty("capabilities");
        Assert.False(capabilities.GetProperty("discoverDotNetProjects").GetBoolean());
        Assert.False(capabilities.GetProperty("calculateRepositoryVersion").GetBoolean());
    }

    private static async Task PullAsync(SyncStateTestContext ctx, int repositoryId)
    {
        await using var scope = ctx.CreateScope();
        var special = await ctx.GetSpecialContextIdAsync();
        string? pageError = null;
        await scope.ServiceProvider.GetRequiredService<WorkspaceCommitSyncHandler>().CommitSyncAsync(
            ctx.WorkspaceId,
            special,
            repositoryId,
            CancellationToken.None,
            new Progress<OperationProgress>(_ => { }),
            (_, _) => { },
            error => pageError = error);
        Assert.Null(pageError);
    }

    /// <summary>
    /// A .NET workspace of a package producer (the seeded repository) and a consumer that references the package,
    /// with the dependency state computed as a Sync would leave it. Returns the consumer's repository id.
    /// </summary>
    private static async Task<int> SeedGraphAsync(SyncStateTestContext ctx, string producerVersion, string consumerReferences)
    {
        await ctx.UseDotNetDependencyProfileAsync();
        var consumerId = await AddConsumerAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
        await writer.ApplyAsync(special, ctx.WorkspaceId, ctx.RepositoryId, SeedSnapshot(
            producerVersion,
            new RepositorySyncProjectNotification
            {
                Name = PackageName,
                ProjectType = 3,
                ProjectPath = "src/Acme.Api/Acme.Api.csproj",
                TargetFramework = "net10.0",
                PackageId = PackageName,
                PackageReferences = [],
            }));
        await writer.ApplyAsync(special, ctx.WorkspaceId, consumerId, SeedSnapshot(
            "1.0.0",
            new RepositorySyncProjectNotification
            {
                Name = "Acme.Web",
                ProjectType = 2,
                ProjectPath = "src/Acme.Web/Acme.Web.csproj",
                TargetFramework = "net10.0",
                PackageReferences = [new RepositorySyncPackageReferenceNotification { Name = PackageName, Version = consumerReferences }],
            }));
        await scope.ServiceProvider.GetRequiredService<WorkspaceStateRecomputeScope>().RecomputeAsync(ctx.WorkspaceId, special);
        return consumerId;
    }

    private static RepositoryStateSnapshot SeedSnapshot(string version, RepositorySyncProjectNotification project) => new()
    {
        BranchName = "main",
        GitVersion = version,
        DefaultBranchName = "main",
        Projects = [project],
        IdentityProbed = true,
        GitVersionProbed = true,
        ProjectsProbed = true,
    };

    private static async Task<int> AddConsumerAsync(SyncStateTestContext ctx)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var producer = await db.Repositories.AsNoTracking().FirstAsync(r => r.RepositoryId == ctx.RepositoryId);
        var consumer = new Repository
        {
            ConnectorId = producer.ConnectorId,
            RepositoryName = ConsumerName,
            OrgName = "acme",
            Visibility = "Public",
            CloneUrl = $"https://github.com/acme/{ConsumerName}.git",
        };
        db.Repositories.Add(consumer);
        await db.SaveChangesAsync();

        db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = consumer.RepositoryId,
            BranchName = "main",
            DefaultBranchName = "main",
            SyncStatus = RepoSyncStatus.InSync,
        });
        await db.SaveChangesAsync();
        return consumer.RepositoryId;
    }

    private static async Task<WorkspaceRepositoryLink> ReadLinkAsync(SyncStateTestContext ctx, int repositoryId)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.WorkspaceRepositories.AsNoTracking()
            .FirstAsync(l => l.WorkspaceId == ctx.WorkspaceId && l.RepositoryId == repositoryId);
    }
}
