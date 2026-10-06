using System.Text.Json;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Services.Git;
using GrayMoon.App.Services.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// The App half of the workspace-profile sync contract: the profile rides out on the request, and an
/// enrichment stage the worker skipped comes back as an absent group rather than an empty one, so the
/// persisted version and project rows survive it.
/// </summary>
public sealed class WorkspaceProfileSyncStateTests
{
    private static readonly object[] OneProject =
    [
        new
        {
            name = "Acme.Api",
            projectType = 1,
            projectPath = "src/Acme.Api/Acme.Api.csproj",
            targetFramework = "net10.0",
            packageId = (string?)null,
            packageReferences = Array.Empty<object>(),
        }
    ];

    private static object SyncResponse(string version, object? projects) => new
    {
        success = true,
        version,
        branch = "main",
        defaultBranch = "main",
        outgoingCommits = 0,
        incomingCommits = 0,
        defaultBranchBehind = 0,
        defaultBranchAhead = 0,
        hasUpstream = true,
        upstreamProbed = true,
        localBranches = new[] { "main" },
        remoteBranches = new[] { "origin/main" },
        tags = Array.Empty<string>(),
        projects,
    };

    [Fact]
    public async Task Sync_sends_the_workspace_profile_capabilities_to_the_worker()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse("-", projects: null));

        await SyncAsync(ctx);

        var sent = ReadCapabilities(ctx, "SyncRepository");
        Assert.False(sent.GetProperty("calculateRepositoryVersion").GetBoolean());
        Assert.False(sent.GetProperty("discoverDotNetProjects").GetBoolean());

        ctx.WorkerBridge.Calls.Clear();
        await SetProfileAsync(ctx, WorkspaceType.DotNetDependency, WorkspaceVersioningMode.GitVersion);
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse("2.0.0", OneProject));

        await SyncAsync(ctx);

        var sentForDotNet = ReadCapabilities(ctx, "SyncRepository");
        Assert.True(sentForDotNet.GetProperty("calculateRepositoryVersion").GetBoolean());
        Assert.True(sentForDotNet.GetProperty("discoverDotNetProjects").GetBoolean());
    }

    [Fact]
    public async Task Skipped_version_and_project_stages_leave_the_persisted_state_alone()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        // A .NET sync first, so there is real version and project state to lose.
        await SetProfileAsync(ctx, WorkspaceType.DotNetDependency, WorkspaceVersioningMode.GitVersion);
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse("2.0.0", OneProject));
        await SyncAsync(ctx);

        Assert.Equal("2.0.0", (await ctx.ReadLinkAsync()).GitVersion);
        Assert.Single(await ctx.ReadProjectsAsync());

        // Then a sync from a worker that ran neither optional stage: no version, and no project block at all.
        await SetProfileAsync(ctx, WorkspaceType.Basic, WorkspaceVersioningMode.None);
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse("-", projects: null));
        await SyncAsync(ctx);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("2.0.0", link.GitVersion);
        Assert.Single(await ctx.ReadProjectsAsync());
        // The common git snapshot still applied.
        Assert.Equal("main", link.BranchName);
        Assert.Equal(0, link.OutgoingCommits);
    }

    [Fact]
    public async Task Genuinely_project_free_repository_prunes_where_a_skipped_scan_does_not()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SetProfileAsync(ctx, WorkspaceType.DotNetDependency, WorkspaceVersioningMode.GitVersion);

        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse("2.0.0", OneProject));
        await SyncAsync(ctx);
        Assert.Single(await ctx.ReadProjectsAsync());

        // An empty list is a probe result: this repository really has no projects now.
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse("2.0.0", Array.Empty<object>()));
        await SyncAsync(ctx);

        Assert.Empty(await ctx.ReadProjectsAsync());
    }

    [Fact]
    public async Task Workspace_without_versioning_is_in_sync_after_a_sync_that_resolved_no_version()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse("-", projects: null));

        await SyncAsync(ctx);

        // Holding a Basic workspace to "every row resolved a version" would leave it permanently out of sync.
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var workspace = await db.Workspaces.AsNoTracking().FirstAsync(w => w.WorkspaceId == ctx.WorkspaceId);
        Assert.True(workspace.IsInSync);
    }

    [Fact]
    public async Task Explicit_state_from_a_hook_distinguishes_a_project_free_repository_from_an_unscanned_one()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SetProfileAsync(ctx, WorkspaceType.DotNetDependency, WorkspaceVersioningMode.GitVersion);
        ctx.WorkerBridge.Respond("SyncRepository", SyncResponse("2.0.0", OneProject));
        await SyncAsync(ctx);
        Assert.Single(await ctx.ReadProjectsAsync());

        // What a pre-State worker sends after a commit in a project-free checkout. The inference
        // (ProjectsProbed = Projects is { Count: > 0 }) cannot call this a scan, so nothing is pruned.
        await HandleHookAsync(ctx, new RepositorySyncNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Version = "2.0.0",
            Branch = "main",
            Projects = [],
        });
        Assert.Single(await ctx.ReadProjectsAsync());

        // The same hook from a current worker says outright that it scanned, so the stale row goes.
        await HandleHookAsync(ctx, new RepositorySyncNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Version = "2.0.0",
            Branch = "main",
            Projects = [],
            State = new RepositoryStateSnapshot
            {
                BranchName = "main",
                GitVersion = "2.0.0",
                Projects = [],
                IdentityProbed = true,
                GitVersionProbed = true,
                ProjectsProbed = true,
            }
        });
        Assert.Empty(await ctx.ReadProjectsAsync());
    }

    [Fact]
    public async Task Explicit_state_that_probed_no_version_keeps_the_persisted_one()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        // A pre-push hook reports identity only. Every other marker is false, so the version, counts and
        // upstream the seeded row already carries all survive.
        await HandleHookAsync(ctx, new RepositorySyncNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Version = "-",
            Branch = "main",
            State = new RepositoryStateSnapshot { BranchName = "main", IdentityProbed = true }
        });

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("main", link.BranchName);
        Assert.Equal("1.0.0", link.GitVersion);
        Assert.Equal(3, link.OutgoingCommits);
        Assert.True(link.BranchHasUpstream);
    }

    private static async Task HandleHookAsync(SyncStateTestContext ctx, RepositorySyncNotification notification)
    {
        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();
        await handler.HandleAsync(notification);
    }

    private static async Task SyncAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var special = await ctx.GetSpecialContextIdAsync();
        await git.SyncAsync(ctx.WorkspaceId, special);
    }

    private static JsonElement ReadCapabilities(SyncStateTestContext ctx, string command)
    {
        var call = Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == command);
        return JsonSerializer.SerializeToElement(call.Args).GetProperty("capabilities");
    }

    private static async Task SetProfileAsync(SyncStateTestContext ctx, WorkspaceType type, WorkspaceVersioningMode versioningMode)
    {
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var workspace = await db.Workspaces.FirstAsync(w => w.WorkspaceId == ctx.WorkspaceId);
        workspace.Type = type;
        workspace.VersioningMode = versioningMode;
        await db.SaveChangesAsync();
    }
}
