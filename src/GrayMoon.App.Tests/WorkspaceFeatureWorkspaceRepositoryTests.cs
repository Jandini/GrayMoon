using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>D10: Feature create, repair retry and remove are two-phase when the Workspace has a Workspace-role repository.</summary>
public sealed class WorkspaceFeatureWorkspaceRepositoryTests
{
    private const string FeatureName = "feat-two-phase";
    private const string RootRepoName = "graymoon-api";
    private const string SourceRepoName = "graymoon-web";
    private const string FeatureRoot = @"C:\Users\test\.graymoon\test-ws\features\feat-two-phase";
    private static readonly string SourceWorktree = FeatureRoot + @"\" + SourceRepoName;

    [Fact]
    public async Task Create_sends_root_worktree_before_any_source_worktree()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedWorkspaceRepositoryAsync(ctx);
        var sourceIds = await AddSourceRepositoriesAsync(ctx, SourceRepoName, "graymoon-ui");
        Assert.Equal(2, sourceIds.Count);
        RespondToCreate(ctx, "graymoon-api", "graymoon-web", "graymoon-ui");

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, FeatureName, WorkspaceFeatureBaseKindApplication.CurrentWorkspace);
        Assert.True(result.Success, result.Error);

        var createCalls = ctx.WorkerBridge.Calls.Where(c => c.Command == WorkerHubMethods.CreateGitWorktree).ToList();
        Assert.Equal(3, createCalls.Count);

        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var featureRoot = await resolver.GetContextRootAsync(result.ContextId!.Value);
        Assert.Equal(featureRoot, WorktreePath(createCalls[0].Args));
        Assert.All(createCalls.Skip(1), c => Assert.NotEqual(featureRoot, WorktreePath(c.Args)));
    }

    [Fact]
    public async Task Create_root_worktree_path_equals_feature_root()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedWorkspaceRepositoryAsync(ctx);
        await AddSourceRepositoriesAsync(ctx, SourceRepoName);
        RespondToCreate(ctx, "graymoon-api", "graymoon-web");

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, FeatureName, WorkspaceFeatureBaseKindApplication.CurrentWorkspace);
        Assert.True(result.Success, result.Error);

        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var featureRoot = await resolver.GetContextRootAsync(result.ContextId!.Value);

        var createCalls = ctx.WorkerBridge.Calls.Where(c => c.Command == WorkerHubMethods.CreateGitWorktree).ToList();
        Assert.Equal(featureRoot, WorktreePath(createCalls[0].Args));
        Assert.Equal(featureRoot + @"\" + SourceRepoName, WorktreePath(createCalls[1].Args));

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var rootRow = await db.WorkspaceFeatureRepositories.SingleAsync(r =>
            r.WorkspaceFeatureContextId == result.ContextId!.Value.Value
            && r.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
        Assert.Equal(featureRoot, rootRow.WorktreePath);
    }

    [Fact]
    public async Task Create_root_failure_leaves_source_rows_pending_and_feature_needs_repair()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedWorkspaceRepositoryAsync(ctx);
        await AddSourceRepositoriesAsync(ctx, SourceRepoName);
        RespondToCreate(ctx, "graymoon-api", "graymoon-web");
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, _ =>
            new WorkerCommandResponse(false, null, "root worktree failed"));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, FeatureName, WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.False(result.Success);
        Assert.Equal("NeedsRepair", result.Condition);
        Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.CreateGitWorktree);

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var feature = await db.WorkspaceFeatures.SingleAsync(f => f.Name == FeatureName);
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, feature.LifecycleState);

        var rows = await db.WorkspaceFeatureRepositories
            .Where(r => r.WorkspaceFeatureContextId == result.ContextId!.Value.Value)
            .ToListAsync();
        var rootRow = Assert.Single(rows, r => r.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
        Assert.Equal(WorkspaceFeatureRepositoryState.NeedsRepair, rootRow.State);
        Assert.Contains("root worktree failed", rootRow.LastError);
        var sourceRow = Assert.Single(rows, r => r.WorkspaceRepositoryId != ctx.WorkspaceRepositoryId);
        Assert.Equal(WorkspaceFeatureRepositoryState.Pending, sourceRow.State);
    }

    [Fact]
    public async Task Remove_sends_root_worktree_after_all_source_worktrees()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedWorkspaceRepositoryAsync(ctx);
        var sourceIds = await AddSourceRepositoriesAsync(ctx, SourceRepoName, "graymoon-ui");
        var featureContextId = await SeedFeatureAsync(
            ctx,
            WorkspaceFeatureLifecycleState.Ready,
            (ctx.WorkspaceRepositoryId, FeatureRoot, WorkspaceFeatureRepositoryState.Ready),
            (sourceIds[0], SourceWorktree, WorkspaceFeatureRepositoryState.Ready),
            (sourceIds[1], FeatureRoot + @"\graymoon-ui", WorkspaceFeatureRepositoryState.Ready));
        RespondToRemove(ctx, _ => new WorkerCommandResponse(true, new { success = true }, null));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(featureContextId, new RemoveFeatureOptions { AllowDiscardUncommitted = true });
        Assert.True(result.Success, result.Error);

        var removeCalls = ctx.WorkerBridge.Calls.Where(c => c.Command == WorkerHubMethods.RemoveGitWorktree).ToList();
        Assert.Equal(3, removeCalls.Count);
        Assert.Equal(FeatureRoot, WorktreePath(removeCalls[2].Args));
        Assert.DoesNotContain(removeCalls.Take(2), c => WorktreePath(c.Args) == FeatureRoot);

        // The root removal deletes the directory itself: no Worker residue cleanup arguments.
        var rootArgs = JsonSerializer.SerializeToElement(removeCalls[2].Args);
        Assert.Equal(JsonValueKind.Null, rootArgs.GetProperty("featureRootPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, rootArgs.GetProperty("featureStorageRoot").ValueKind);
    }

    [Fact]
    public async Task Remove_skips_root_when_a_source_removal_failed()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedWorkspaceRepositoryAsync(ctx);
        var sourceIds = await AddSourceRepositoriesAsync(ctx, SourceRepoName);
        var featureContextId = await SeedFeatureAsync(
            ctx,
            WorkspaceFeatureLifecycleState.Ready,
            (ctx.WorkspaceRepositoryId, FeatureRoot, WorkspaceFeatureRepositoryState.Ready),
            (sourceIds[0], SourceWorktree, WorkspaceFeatureRepositoryState.Ready));
        RespondToRemove(ctx, args => WorktreePath(args) == SourceWorktree
            ? new WorkerCommandResponse(false, null, "Permission denied on graymoon-web")
            : new WorkerCommandResponse(true, new { success = true }, null));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RemoveFeatureAsync(featureContextId, new RemoveFeatureOptions { AllowDiscardUncommitted = true });

        Assert.False(result.Success);
        Assert.Contains("Permission denied", result.Error);
        Assert.Contains("Workspace repository worktree kept until all repository worktrees are removed.", result.Error);

        var removeCall = Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.RemoveGitWorktree);
        Assert.Equal(SourceWorktree, WorktreePath(removeCall.Args));

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var feature = await db.WorkspaceFeatures.SingleAsync(f => f.Name == FeatureName);
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, feature.LifecycleState);
        var rootRow = await db.WorkspaceFeatureRepositories.SingleAsync(r =>
            r.WorkspaceFeatureContextId == featureContextId.Value && r.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
        Assert.NotEqual(WorkspaceFeatureRepositoryState.Removed, rootRow.State);
    }

    [Fact]
    public async Task Retry_retries_root_before_sources()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedWorkspaceRepositoryAsync(ctx);
        var sourceIds = await AddSourceRepositoriesAsync(ctx, SourceRepoName, "graymoon-ui");
        var featureContextId = await SeedFeatureAsync(
            ctx,
            WorkspaceFeatureLifecycleState.NeedsRepair,
            (sourceIds[0], SourceWorktree, WorkspaceFeatureRepositoryState.Pending),
            (sourceIds[1], FeatureRoot + @"\graymoon-ui", WorkspaceFeatureRepositoryState.Pending),
            (ctx.WorkspaceRepositoryId, FeatureRoot, WorkspaceFeatureRepositoryState.NeedsRepair));
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, args =>
            new WorkerCommandResponse(true, new { success = true, worktreePath = WorktreePath(args) }, null));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RepairFeatureAsync(featureContextId);
        Assert.True(result.Success, result.Error);

        var createCalls = ctx.WorkerBridge.Calls.Where(c => c.Command == WorkerHubMethods.CreateGitWorktree).ToList();
        Assert.Equal(3, createCalls.Count);
        Assert.Equal(FeatureRoot, WorktreePath(createCalls[0].Args));
    }

    [Fact]
    public async Task Workspace_without_workspace_repository_behaves_exactly_as_before()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await AddSourceRepositoriesAsync(ctx, SourceRepoName);
        RespondToCreate(ctx, "graymoon-api", "graymoon-web");

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var created = await ops.CreateFeatureAsync(ctx.WorkspaceId, FeatureName, WorkspaceFeatureBaseKindApplication.CurrentWorkspace);
        Assert.True(created.Success, created.Error);

        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var featureRoot = await resolver.GetContextRootAsync(created.ContextId!.Value);

        // Create first finishes any folder a removed Feature of the same name left pending deletion.
        var commands = ctx.WorkerBridge.Calls.Select(c => c.Command).Where(c => c != WorkerHubMethods.CleanupFeatureFolder).ToList();
        Assert.Equal(WorkerHubMethods.GetHeadCommits, commands[0]);
        var createCalls = ctx.WorkerBridge.Calls.Where(c => c.Command == WorkerHubMethods.CreateGitWorktree).ToList();
        Assert.Equal(2, createCalls.Count);
        Assert.Equal(
            new[] { featureRoot + @"\graymoon-api", featureRoot + @"\graymoon-web" },
            createCalls.Select(c => WorktreePath(c.Args)).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        var headArgs = JsonSerializer.SerializeToElement(ctx.WorkerBridge.Calls.First(c => c.Command == WorkerHubMethods.GetHeadCommits).Args);
        Assert.Equal(JsonValueKind.Null, headArgs.GetProperty("workspaceRepositoryName").ValueKind);

        ctx.WorkerBridge.Calls.Clear();
        RespondToRemove(ctx, _ => new WorkerCommandResponse(true, new { success = true }, null));
        var removed = await ops.RemoveFeatureAsync(created.ContextId!.Value, new RemoveFeatureOptions { AllowDiscardUncommitted = true });
        Assert.True(removed.Success, removed.Error);

        var removeCalls = ctx.WorkerBridge.Calls.Where(c => c.Command == WorkerHubMethods.RemoveGitWorktree).ToList();
        Assert.Equal(2, removeCalls.Count);
        Assert.All(removeCalls, c =>
        {
            var args = JsonSerializer.SerializeToElement(c.Args);
            Assert.Equal(featureRoot, args.GetProperty("featureRootPath").GetString());
        });
    }

    [Fact]
    public async Task Rollback_removes_root_worktree_after_all_source_worktrees()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedWorkspaceRepositoryAsync(ctx);
        var sourceIds = await AddSourceRepositoriesAsync(ctx, SourceRepoName, "graymoon-ui");
        var featureContextId = await SeedFeatureAsync(
            ctx,
            WorkspaceFeatureLifecycleState.NeedsRepair,
            (ctx.WorkspaceRepositoryId, FeatureRoot, WorkspaceFeatureRepositoryState.Ready),
            (sourceIds[0], SourceWorktree, WorkspaceFeatureRepositoryState.Ready),
            (sourceIds[1], FeatureRoot + @"\graymoon-ui", WorkspaceFeatureRepositoryState.Ready));
        RespondToRollback(ctx, _ => new WorkerCommandResponse(true, new { success = true }, null));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RollbackFeatureAsync(featureContextId);
        Assert.True(result.Success, result.Error);

        var removeCalls = ctx.WorkerBridge.Calls.Where(c => c.Command == WorkerHubMethods.RemoveGitWorktree).ToList();
        Assert.Equal(3, removeCalls.Count);
        Assert.Equal(FeatureRoot, WorktreePath(removeCalls[2].Args));
        Assert.DoesNotContain(removeCalls.Take(2), c => WorktreePath(c.Args) == FeatureRoot);

        var rootArgs = JsonSerializer.SerializeToElement(removeCalls[2].Args);
        Assert.Equal(JsonValueKind.Null, rootArgs.GetProperty("featureRootPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, rootArgs.GetProperty("featureStorageRoot").ValueKind);
    }

    [Fact]
    public async Task Rollback_keeps_root_when_a_source_removal_failed()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SeedWorkspaceRepositoryAsync(ctx);
        var sourceIds = await AddSourceRepositoriesAsync(ctx, SourceRepoName);
        var featureContextId = await SeedFeatureAsync(
            ctx,
            WorkspaceFeatureLifecycleState.NeedsRepair,
            (ctx.WorkspaceRepositoryId, FeatureRoot, WorkspaceFeatureRepositoryState.Ready),
            (sourceIds[0], SourceWorktree, WorkspaceFeatureRepositoryState.Ready));
        RespondToRollback(ctx, args => WorktreePath(args) == SourceWorktree
            ? new WorkerCommandResponse(false, null, "Permission denied on graymoon-web")
            : new WorkerCommandResponse(true, new { success = true }, null));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.RollbackFeatureAsync(featureContextId);

        Assert.False(result.Success);
        Assert.Contains("Permission denied", result.Error);
        Assert.Contains("Workspace repository worktree kept until all repository worktrees are removed.", result.Error);
        var removeCall = Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.RemoveGitWorktree);
        Assert.Equal(SourceWorktree, WorktreePath(removeCall.Args));

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.WorkspaceFeatureRepositories.AnyAsync(r =>
            r.WorkspaceFeatureContextId == featureContextId.Value && r.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId));
    }

    [Fact]
    public async Task Rollback_without_workspace_repository_behaves_exactly_as_before()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var sourceIds = await AddSourceRepositoriesAsync(ctx, SourceRepoName);
        var featureContextId = await SeedFeatureAsync(
            ctx,
            WorkspaceFeatureLifecycleState.NeedsRepair,
            (ctx.WorkspaceRepositoryId, FeatureRoot + @"\graymoon-api", WorkspaceFeatureRepositoryState.Ready),
            (sourceIds[0], SourceWorktree, WorkspaceFeatureRepositoryState.Ready));
        RespondToRollback(ctx, _ => new WorkerCommandResponse(true, new { success = true }, null));

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceContextPathResolver>();
        var featureRoot = await resolver.GetContextRootAsync(featureContextId);
        var result = await ops.RollbackFeatureAsync(featureContextId);
        Assert.True(result.Success, result.Error);

        var mutating = ctx.WorkerBridge.Calls
            .Where(c => c.Command is WorkerHubMethods.RemoveGitWorktree or "DeleteBranch")
            .ToList();
        Assert.Equal(
            new[]
            {
                WorkerHubMethods.RemoveGitWorktree, "DeleteBranch",
                WorkerHubMethods.RemoveGitWorktree, "DeleteBranch",
            },
            mutating.Select(c => c.Command).ToArray());
        Assert.Equal(FeatureRoot + @"\graymoon-api", WorktreePath(mutating[0].Args));
        Assert.Equal(SourceWorktree, WorktreePath(mutating[2].Args));
        Assert.All(
            mutating.Where(c => c.Command == WorkerHubMethods.RemoveGitWorktree),
            c => Assert.Equal(featureRoot, JsonSerializer.SerializeToElement(c.Args).GetProperty("featureRootPath").GetString()));
    }

    private static void RespondToRollback(SyncStateTestContext ctx, Func<object, WorkerCommandResponse> removeHandler)
    {
        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, new
        {
            exists = true,
            isDirty = false,
            hasUpstream = true,
            aheadOfUpstream = 0,
            aheadOfDefault = 0,
            branch = FeatureName,
        });
        ctx.WorkerBridge.Respond(WorkerHubMethods.ListGitWorktrees, args =>
        {
            var main = JsonSerializer.SerializeToElement(args).GetProperty("mainRepositoryPath").GetString() ?? "";
            return new WorkerCommandResponse(true, new
            {
                worktrees = new object[]
                {
                    new { worktreePath = main, branchName = "main", isBare = false },
                    new { worktreePath = FeatureRoot, branchName = FeatureName, isBare = false },
                    new { worktreePath = FeatureRoot + @"\graymoon-api", branchName = FeatureName, isBare = false },
                    new { worktreePath = SourceWorktree, branchName = FeatureName, isBare = false },
                    new { worktreePath = FeatureRoot + @"\graymoon-ui", branchName = FeatureName, isBare = false },
                },
            }, null);
        });
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, removeHandler);
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });
    }

    private static string WorktreePath(object args) =>
        JsonSerializer.SerializeToElement(args).GetProperty("worktreePath").GetString() ?? "";

    private static void RespondToCreate(SyncStateTestContext ctx, params string[] repositoryNames)
    {
        var commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in repositoryNames)
        {
            commits[name] = "abc123def456abc123def456abc123def456abc1";
            branches[name] = "develop";
        }

        ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new { commits, branches });
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, args =>
            new WorkerCommandResponse(true, new { success = true, worktreePath = WorktreePath(args) }, null));
    }

    private static void RespondToRemove(SyncStateTestContext ctx, Func<object, WorkerCommandResponse> removeHandler)
    {
        ctx.WorkerBridge.Respond(WorkerHubMethods.InspectWorktree, new
        {
            exists = true,
            isDirty = false,
            hasUpstream = true,
            aheadOfUpstream = 0,
            aheadOfDefault = 0,
            branch = FeatureName,
            featureBranchExists = true,
            featureBranchAheadOfDefault = 0,
            featureBranchHasUpstream = true,
            featureBranchAheadOfUpstream = 0,
        });
        ctx.WorkerBridge.Respond(WorkerHubMethods.RemoveGitWorktree, removeHandler);
        ctx.WorkerBridge.Respond("DeleteBranch", new { success = true });
    }

    private static async Task SeedWorkspaceRepositoryAsync(SyncStateTestContext ctx) =>
        await ctx.MutateLinkAsync(link => link.Role = WorkspaceRepositoryRole.Workspace);

    private static async Task<List<int>> AddSourceRepositoriesAsync(SyncStateTestContext ctx, params string[] repositoryNames)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connectorId = await db.Repositories
            .Where(r => r.RepositoryId == ctx.RepositoryId)
            .Select(r => r.ConnectorId)
            .SingleAsync();

        var ids = new List<int>();
        foreach (var repositoryName in repositoryNames)
        {
            var repo = new Repository
            {
                ConnectorId = connectorId,
                RepositoryName = repositoryName,
                OrgName = "acme",
                Visibility = "Public",
                CloneUrl = $"https://github.com/acme/{repositoryName}.git",
            };
            db.Repositories.Add(repo);
            await db.SaveChangesAsync();

            var link = new WorkspaceRepositoryLink
            {
                WorkspaceId = ctx.WorkspaceId,
                RepositoryId = repo.RepositoryId,
                GitVersion = "1.0.0",
                BranchName = "main",
                DefaultBranchName = "main",
                OutgoingCommits = 0,
                IncomingCommits = 0,
                BranchHasUpstream = true,
                SyncStatus = RepoSyncStatus.InSync,
                RepositoryType = ProjectType.Library,
                Role = WorkspaceRepositoryRole.Source,
            };
            db.WorkspaceRepositories.Add(link);
            await db.SaveChangesAsync();
            ids.Add(link.WorkspaceRepositoryId);
        }

        return ids;
    }

    private static async Task<WorkspaceFeatureContextId> SeedFeatureAsync(
        SyncStateTestContext ctx,
        WorkspaceFeatureLifecycleState lifecycle,
        params (int WorkspaceRepositoryId, string WorktreePath, WorkspaceFeatureRepositoryState State)[] rows)
    {
        await using var scope = ctx.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var feature = new WorkspaceFeature
        {
            WorkspaceId = ctx.WorkspaceId,
            Name = FeatureName,
            LifecycleState = lifecycle,
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

        foreach (var row in rows)
        {
            db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = row.WorkspaceRepositoryId,
                WorktreePath = row.WorktreePath,
                State = row.State,
                BaseCommitSha = "abc123",
                ParentBranchName = "main",
                CreatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }
}
