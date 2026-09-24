using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

public sealed class FeatureContextIsolationTests
{
    [Fact]
    public async Task Project_merge_for_feature_does_not_clear_workspace_or_other_feature()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var projects = scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>();
        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();

        var special = await resolver.GetOrCreateSpecialWorkspaceContextIdAsync(ctx.WorkspaceId);
        var featureA = await CreateFeatureContextAsync(db, ctx.WorkspaceId, "feat-a");
        var featureB = await CreateFeatureContextAsync(db, ctx.WorkspaceId, "feat-b");

        await projects.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.RepositoryId,
            [new SyncProjectInfo("WsProj", ProjectType.Library, "Ws.csproj", "net10.0", null, [])],
            special.Value);
        await projects.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.RepositoryId,
            [new SyncProjectInfo("AProj", ProjectType.Library, "A.csproj", "net10.0", null, [])],
            featureA.Value);
        await projects.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.RepositoryId,
            [new SyncProjectInfo("BProj", ProjectType.Library, "B.csproj", "net10.0", null, [])],
            featureB.Value);

        await projects.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.RepositoryId,
            [new SyncProjectInfo("AProj2", ProjectType.Library, "A2.csproj", "net10.0", null, [])],
            featureA.Value);

        var wsCount = await db.WorkspaceProjects.CountAsync(p => p.WorkspaceFeatureContextId == special.Value);
        var aNames = await db.WorkspaceProjects.Where(p => p.WorkspaceFeatureContextId == featureA.Value)
            .Select(p => p.ProjectName).ToListAsync();
        var bNames = await db.WorkspaceProjects.Where(p => p.WorkspaceFeatureContextId == featureB.Value)
            .Select(p => p.ProjectName).ToListAsync();

        Assert.Equal(1, wsCount);
        Assert.Equal(["AProj2"], aNames);
        Assert.Equal(["BProj"], bNames);
    }

    [Fact]
    public async Task Same_project_name_can_exist_in_workspace_and_feature()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var projects = scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>();
        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();

        var special = await resolver.GetOrCreateSpecialWorkspaceContextIdAsync(ctx.WorkspaceId);
        var feature = await CreateFeatureContextAsync(db, ctx.WorkspaceId, "feat-shared");
        var incoming = new SyncProjectInfo("Shared", ProjectType.Library, "Shared.csproj", "net10.0", null, []);

        await projects.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.RepositoryId, [incoming], special.Value);
        await projects.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.RepositoryId, [incoming], feature.Value);

        var names = await db.WorkspaceProjects
            .Where(p => p.WorkspaceId == ctx.WorkspaceId && p.ProjectName == "Shared")
            .Select(p => p.WorkspaceFeatureContextId)
            .ToListAsync();
        Assert.Equal(2, names.Count);
        Assert.Contains(special.Value, names);
        Assert.Contains(feature.Value, names);
    }

    [Fact]
    public async Task File_line_statuses_are_scoped_per_context()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();

        var special = await resolver.GetOrCreateSpecialWorkspaceContextIdAsync(ctx.WorkspaceId);
        var featureA = await CreateFeatureContextAsync(db, ctx.WorkspaceId, "feat-a");

        db.WorkspaceFileLineStatuses.Add(new WorkspaceFileLineStatus
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            FilePath = "a.txt",
            FileName = "a.txt",
            TokenName = "x",
            CurrentValue = "1",
            ExpectedValue = "2",
            WorkspaceFeatureContextId = special.Value
        });
        db.WorkspaceFileLineStatuses.Add(new WorkspaceFileLineStatus
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            FilePath = "a.txt",
            FileName = "a.txt",
            TokenName = "y",
            CurrentValue = "3",
            ExpectedValue = "4",
            WorkspaceFeatureContextId = featureA.Value
        });
        await db.SaveChangesAsync();

        var specialRows = await db.WorkspaceFileLineStatuses
            .CountAsync(s => s.WorkspaceFeatureContextId == special.Value);
        var featureRows = await db.WorkspaceFileLineStatuses
            .CountAsync(s => s.WorkspaceFeatureContextId == featureA.Value);

        Assert.Equal(1, specialRows);
        Assert.Equal(1, featureRows);
    }

    [Fact]
    public async Task Path_resolver_feature_root_differs_from_special_workspace()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var workspaceService = scope.ServiceProvider.GetRequiredService<WorkspaceService>();

        var special = await resolver.GetOrCreateSpecialWorkspaceContextIdAsync(ctx.WorkspaceId);
        var feature = await CreateFeatureContextAsync(db, ctx.WorkspaceId, "feat-path");

        await using var seedDb = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>()
            .CreateDbContextAsync();
        var workspace = await seedDb.Workspaces.FirstAsync(w => w.WorkspaceId == ctx.WorkspaceId);
        workspace.RootPath = @"C:\Workspace";
        workspace.ManagedFeatureStorageRoot = @"C:\Workspace\.graymoon\test-ws\features";
        await seedDb.SaveChangesAsync();

        var (specialRoot, specialFolder) = await pathResolver.GetAgentWorkspaceArgsAsync(special);
        var (featureRoot, featureFolder) = await pathResolver.GetAgentWorkspaceArgsAsync(feature);

        Assert.Equal("test-ws", specialFolder);
        Assert.Equal("feat-path", featureFolder);
        Assert.False(string.Equals(specialRoot, featureRoot, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(".graymoon", featureRoot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("feat-path", specialRoot, StringComparison.OrdinalIgnoreCase);

        var structuralRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace);
        Assert.Equal(@"C:\Workspace", structuralRoot);
    }

    [Fact]
    public async Task Path_resolver_repository_path_uses_feature_worktree_when_present()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();

        var feature = await CreateFeatureContextAsync(db, ctx.WorkspaceId, "feat-wt");
        const string worktreePath = @"C:\Workspace\.graymoon\test-ws\features\feat-wt\graymoon-api";

        db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
        {
            WorkspaceFeatureContextId = feature.Value,
            WorkspaceRepositoryId = ctx.WorkspaceRepositoryId,
            WorktreePath = worktreePath,
            State = WorkspaceFeatureRepositoryState.Ready,
            BaseCommitSha = "abc123",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var resolved = await pathResolver.GetRepositoryPathAsync(feature, ctx.WorkspaceRepositoryId);
        Assert.Equal(worktreePath, resolved);

        var special = await resolver.GetOrCreateSpecialWorkspaceContextIdAsync(ctx.WorkspaceId);
        await using var seedDb = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>()
            .CreateDbContextAsync();
        var workspace = await seedDb.Workspaces.FirstAsync(w => w.WorkspaceId == ctx.WorkspaceId);
        workspace.RootPath = @"C:\Workspace";
        await seedDb.SaveChangesAsync();

        var specialRepoPath = await pathResolver.GetRepositoryPathAsync(special, ctx.WorkspaceRepositoryId);
        Assert.DoesNotContain("feat-wt", specialRepoPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.Equals(worktreePath, specialRepoPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Context_job_keys_parse_context_id()
    {
        Assert.True(WorkspaceJobKeys.TryGetContextId("/workspaces/3/ctx/9/changes", out var ws, out var ctxId));
        Assert.Equal(3, ws);
        Assert.Equal(9, ctxId);
        Assert.False(WorkspaceJobKeys.TryGetContextId("/workspaces/3/changes", out _, out _));
    }

    private const string SeededFeatureStorageRoot = @"C:\Users\test\.graymoon";

    [Fact]
    public async Task Path_resolver_feature_root_uses_configured_feature_storage_setting()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();

        var feature = await CreateFeatureContextAsync(db, ctx.WorkspaceId, "feat-drive");

        await using var seedDb = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>()
            .CreateDbContextAsync();
        var workspace = await seedDb.Workspaces.FirstAsync(w => w.WorkspaceId == ctx.WorkspaceId);
        workspace.RootPath = @"C:\Workspace";
        workspace.ManagedFeatureStorageRoot = null;
        await seedDb.SaveChangesAsync();

        var featureRoot = await pathResolver.GetContextRootAsync(feature);
        Assert.StartsWith(SeededFeatureStorageRoot + @"\test-ws\features\", featureRoot.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@":\.graymoon", featureRoot.Replace('/', '\\').Substring(1), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Path_resolver_ignores_persisted_drive_root_graymoon()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();

        var feature = await CreateFeatureContextAsync(db, ctx.WorkspaceId, "feat-legacy");

        await using var seedDb = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>()
            .CreateDbContextAsync();
        var workspace = await seedDb.Workspaces.FirstAsync(w => w.WorkspaceId == ctx.WorkspaceId);
        workspace.RootPath = @"C:\Workspace";
        workspace.ManagedFeatureStorageRoot = @"C:\.graymoon\test-ws\features";
        await seedDb.SaveChangesAsync();

        var featureRoot = await pathResolver.GetContextRootAsync(feature);
        Assert.StartsWith(SeededFeatureStorageRoot + @"\test-ws\features\", featureRoot.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\.graymoon", featureRoot.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Path_resolver_keeps_persisted_root_when_feature_storage_setting_changes()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var settings = scope.ServiceProvider.GetRequiredService<AppSettingRepository>();
        var workspaceService = scope.ServiceProvider.GetRequiredService<WorkspaceService>();

        var feature = await CreateFeatureContextAsync(db, ctx.WorkspaceId, "feat-stable");
        const string originalRoot = @"D:\old-graymoon\test-ws\features";

        await using var seedDb = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>()
            .CreateDbContextAsync();
        var workspace = await seedDb.Workspaces.FirstAsync(w => w.WorkspaceId == ctx.WorkspaceId);
        workspace.ManagedFeatureStorageRoot = originalRoot;
        await seedDb.SaveChangesAsync();

        await settings.SetValueAsync(AppSettingRepository.FeatureStorageRootPathKey, @"E:\new-graymoon");
        workspaceService.ClearCachedFeatureStorageRootPath();

        var featureRoot = await pathResolver.GetContextRootAsync(feature);
        Assert.Equal(originalRoot + @"\feat-stable", featureRoot.Replace('/', '\\'));
    }

    [Fact]
    public async Task Create_feature_persists_configured_storage_root_and_relocates_drive_root_graymoon()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var workspace = await db.Workspaces.FirstAsync(w => w.WorkspaceId == ctx.WorkspaceId);
            workspace.RootPath = @"C:\Workspace";
            // Legacy bad path from GetWindowsDirectoryName(RootPath) bug — must be relocated.
            workspace.ManagedFeatureStorageRoot = @"C:\.graymoon\test-ws\features";
            await db.SaveChangesAsync();
        }

        ctx.AgentBridge.Respond(AgentHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "abc123def456abc123def456abc123def456abc1",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "main",
            },
        });
        ctx.AgentBridge.Respond(AgentHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:\wt" });

        await using var opsScope = ctx.CreateScope();
        var ops = opsScope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, "feat-relocate", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);
        Assert.True(result.Success, result.Error);

        await using var read = ctx.CreateScope();
        var readDb = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var updated = await readDb.Workspaces.AsNoTracking().FirstAsync(w => w.WorkspaceId == ctx.WorkspaceId);
        Assert.False(string.IsNullOrWhiteSpace(updated.ManagedFeatureStorageRoot));
        Assert.Equal(SeededFeatureStorageRoot + @"\test-ws\features", updated.ManagedFeatureStorageRoot!.Replace('/', '\\'));
        Assert.False(
            updated.ManagedFeatureStorageRoot.Replace('/', '\\')
                .StartsWith(@"C:\.graymoon", StringComparison.OrdinalIgnoreCase));

        // Agent receives the configured storage path (CreateGitWorktree response may overwrite the stored path).
        var createCall = Assert.Single(ctx.AgentBridge.Calls, c => c.Command == AgentHubMethods.CreateGitWorktree);
        var worktreePathProp = createCall.Args.GetType().GetProperty("worktreePath");
        Assert.NotNull(worktreePathProp);
        var requestedPath = Assert.IsType<string>(worktreePathProp!.GetValue(createCall.Args));
        Assert.StartsWith(SeededFeatureStorageRoot + @"\test-ws\features\", requestedPath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\.graymoon", requestedPath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<WorkspaceFeatureContextId> CreateFeatureContextAsync(
        AppDbContext db, int workspaceId, string name)
    {
        var feature = new WorkspaceFeature
        {
            WorkspaceId = workspaceId,
            Name = name,
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = workspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();
        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }
}
