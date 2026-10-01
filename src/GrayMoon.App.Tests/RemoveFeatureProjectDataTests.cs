using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// B1: removing a Feature must delete its own WorkspaceProjects, ProjectDependencies and
/// WorkspaceFileLineStatuses in the same remove operation, and must never touch another
/// Feature context's rows or the special Workspace context row (feature-context-scoping.mdc).
/// </summary>
public sealed class RemoveFeatureProjectDataTests
{
    private static object CleanInspectWorktree() => new
    {
        exists = true,
        isDirty = false,
        hasUpstream = true,
        aheadOfUpstream = 0,
        aheadOfDefault = 0,
    };

    private static object SyncResponse() => new
    {
        success = true,
        version = "2.0.0",
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
        projects = Array.Empty<object>(),
    };

    /// <summary>
    /// Agent responses for the post-remove Workspace status refresh (RefreshWorkspaceStateAfterFeatureRemoveAsync).
    /// This refresh independently reconciles the special Workspace context's own WorkspaceProjects and
    /// WorkspaceFileLineStatuses from live Agent data - behaviour that exists before B1 and is already
    /// covered by RemoveFeatureWorkspaceRefreshTests. These responses report an empty project/file set
    /// (nothing configured in this fixture) so that refresh is not itself under test here.
    /// </summary>
    private static void RespondCleanRemoval(SyncStateTestContext ctx)
    {
        ctx.AgentBridge.Respond(AgentHubMethods.InspectWorktree, CleanInspectWorktree());
        ctx.AgentBridge.Respond(AgentHubMethods.RemoveGitWorktree, new { success = true });
        ctx.AgentBridge.Respond("DeleteBranch", new { success = true });
        ctx.AgentBridge.Respond("SyncRepository", SyncResponse());
        ctx.AgentBridge.Respond("CheckFileVersions", new { success = true, files = Array.Empty<object>() });
    }

    [Fact]
    public async Task Remove_deletes_only_the_removed_contexts_project_data()
    {
        // Seed two Feature contexts, each with its own WorkspaceProjects, a ProjectDependency and a
        // WorkspaceFileLineStatus row. Removing one Feature must delete only its own rows and leave
        // the other Feature context's rows byte-for-byte untouched, and must never remove the special
        // Workspace context row itself.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var keepFeatureContextId = await SeedFeatureContextAsync(ctx, "feat-keep");
        var removeFeatureContextId = await SeedFeatureContextAsync(ctx, "feat-remove");
        var specialContextId = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedProjectDataAsync(db, ctx.WorkspaceId, ctx.RepositoryId, keepFeatureContextId.Value, "keep");
            await SeedProjectDataAsync(db, ctx.WorkspaceId, ctx.RepositoryId, removeFeatureContextId.Value, "remove");
        }

        RespondCleanRemoval(ctx);

        await using var opScope = ctx.CreateScope();
        var ops = opScope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            removeFeatureContextId,
            new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowForceDeleteLocalBranches = true });

        Assert.True(result.Success, result.Error);

        await using var readScope = ctx.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();

        await AssertContextDataGoneAsync(readDb, removeFeatureContextId.Value);
        await AssertContextDataIntactAsync(readDb, keepFeatureContextId.Value, "keep");

        var workspaceContext = await readDb.WorkspaceFeatureContexts
            .AsNoTracking()
            .SingleAsync(c => c.WorkspaceFeatureContextId == specialContextId.Value);
        Assert.Equal(WorkspaceFeatureContextKind.Workspace, workspaceContext.Kind);
    }

    [Fact]
    public async Task Remove_deletes_project_data_even_when_the_database_has_no_foreign_key_enforcement()
    {
        // Upgraded real databases may lack the physical foreign key / cascade that the current EF
        // model declares (older migrations added columns without recreating constraints). The
        // explicit delete must not depend on SQLite foreign-key cascade to do this work.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var keepFeatureContextId = await SeedFeatureContextAsync(ctx, "feat-keep");
        var removeFeatureContextId = await SeedFeatureContextAsync(ctx, "feat-remove");

        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            await SeedProjectDataAsync(db, ctx.WorkspaceId, ctx.RepositoryId, keepFeatureContextId.Value, "keep");
            await SeedProjectDataAsync(db, ctx.WorkspaceId, ctx.RepositoryId, removeFeatureContextId.Value, "remove");
        }

        RespondCleanRemoval(ctx);

        await using var opScope = ctx.CreateScope();
        var ops = opScope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(
            removeFeatureContextId,
            new RemoveFeatureOptions { AllowDiscardUncommitted = true, AllowForceDeleteLocalBranches = true });

        Assert.True(result.Success, result.Error);

        await using var readScope = ctx.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();

        await AssertContextDataGoneAsync(readDb, removeFeatureContextId.Value);
        await AssertContextDataIntactAsync(readDb, keepFeatureContextId.Value, "keep");
    }

    private static async Task<WorkspaceFeatureContextId> SeedFeatureContextAsync(SyncStateTestContext ctx, string featureName)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = featureName,
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

        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            WorktreePath = $@"C:\gm-test-root\.graymoon\test-ws\features\{featureName}\graymoon-api",
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "abc123",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }

    private static async Task SeedProjectDataAsync(
        AppDbContext db,
        int workspaceId,
        int repositoryId,
        int contextId,
        string suffix)
    {
        var dependent = new WorkspaceProject
        {
            WorkspaceId = workspaceId,
            WorkspaceFeatureContextId = contextId,
            RepositoryId = repositoryId,
            ProjectName = $"ProjA-{suffix}",
            ProjectType = ProjectType.Library,
            ProjectFilePath = $@"src\ProjA-{suffix}\ProjA-{suffix}.csproj",
            TargetFramework = "net9.0",
        };
        var referenced = new WorkspaceProject
        {
            WorkspaceId = workspaceId,
            WorkspaceFeatureContextId = contextId,
            RepositoryId = repositoryId,
            ProjectName = $"ProjB-{suffix}",
            ProjectType = ProjectType.Library,
            ProjectFilePath = $@"src\ProjB-{suffix}\ProjB-{suffix}.csproj",
            TargetFramework = "net9.0",
        };
        db.WorkspaceProjects.Add(dependent);
        db.WorkspaceProjects.Add(referenced);
        await db.SaveChangesAsync();

        db.ProjectDependencies.Add(new ProjectDependency
        {
            DependentProjectId = dependent.ProjectId,
            ReferencedProjectId = referenced.ProjectId,
            Version = "1.0.0",
        });
        await db.SaveChangesAsync();

        db.WorkspaceFileLineStatuses.Add(new WorkspaceFileLineStatus
        {
            WorkspaceId = workspaceId,
            WorkspaceFeatureContextId = contextId,
            RepositoryId = repositoryId,
            FilePath = $"file-{suffix}.txt",
            FileName = $"file-{suffix}.txt",
            TokenName = $"token-{suffix}",
            CurrentValue = "1.0.0",
            ExpectedValue = "1.0.0",
        });
        await db.SaveChangesAsync();
    }

    private static async Task AssertContextDataGoneAsync(AppDbContext db, int contextId)
    {
        Assert.False(await db.WorkspaceProjects.AnyAsync(p => p.WorkspaceFeatureContextId == contextId));
        Assert.False(await db.WorkspaceFileLineStatuses.AnyAsync(s => s.WorkspaceFeatureContextId == contextId));
        Assert.False(await db.ProjectDependencies.AnyAsync(d =>
            d.DependentProject!.WorkspaceFeatureContextId == contextId
            || d.ReferencedProject!.WorkspaceFeatureContextId == contextId));
    }

    private static async Task AssertContextDataIntactAsync(AppDbContext db, int contextId, string suffix)
    {
        var projects = await db.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceFeatureContextId == contextId)
            .OrderBy(p => p.ProjectName)
            .ToListAsync();
        Assert.Equal(2, projects.Count);
        Assert.Equal($"ProjA-{suffix}", projects[0].ProjectName);
        Assert.Equal($"ProjB-{suffix}", projects[1].ProjectName);

        var projectIds = projects.Select(p => p.ProjectId).ToList();
        var dependencyCount = await db.ProjectDependencies
            .AsNoTracking()
            .CountAsync(d => projectIds.Contains(d.DependentProjectId) && projectIds.Contains(d.ReferencedProjectId));
        Assert.Equal(1, dependencyCount);

        var statuses = await db.WorkspaceFileLineStatuses
            .AsNoTracking()
            .Where(s => s.WorkspaceFeatureContextId == contextId)
            .ToListAsync();
        var status = Assert.Single(statuses);
        Assert.Equal($"file-{suffix}.txt", status.FilePath);
        Assert.Equal($"token-{suffix}", status.TokenName);
    }
}
