using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>C1: Create Feature persists Feature + context + Pending rows together or not at all.</summary>
public sealed class CreateFeatureIntentAtomicityTests
{
    [Fact]
    public async Task Head_commits_incomplete_writes_no_Feature_row()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        // Count matches (one repo) but SHA is blank - previously passed the Count check,
        // saved Feature + context, then returned HeadCommitsIncomplete and left ghost rows.
        ctx.AgentBridge.Respond(AgentHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "   ",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "develop",
            },
        });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(
            ctx.WorkspaceId,
            "feature/incomplete-heads",
            WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.False(result.Success);
        Assert.Equal("HeadCommitsIncomplete", result.Condition);

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WorkspaceFeatures.AnyAsync(f =>
            f.WorkspaceId == ctx.WorkspaceId && f.Name == "feature/incomplete-heads"));
        Assert.False(await db.WorkspaceFeatureContexts.AnyAsync(c =>
            c.WorkspaceId == ctx.WorkspaceId && c.Kind == WorkspaceFeatureContextKind.Feature));
        Assert.Empty(await db.WorkspaceFeatureRepositories.ToListAsync());
    }

    [Fact]
    public async Task Exception_while_saving_repo_rows_writes_no_Feature_or_context_row()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o =>
            o.AddInterceptors(new ThrowOnFeatureRepositoryInsertInterceptor()));

        ctx.AgentBridge.Respond(AgentHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "develop",
            },
        });
        ctx.AgentBridge.Respond(AgentHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:\wt" });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(
            ctx.WorkspaceId,
            "feature/save-rollback",
            WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.False(result.Success);
        Assert.Equal("Exception", result.Condition);
        Assert.Contains("Simulated save failure", result.Error, StringComparison.Ordinal);

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.WorkspaceFeatures.AnyAsync(f =>
            f.WorkspaceId == ctx.WorkspaceId && f.Name == "feature/save-rollback"));
        Assert.False(await db.WorkspaceFeatureContexts.AnyAsync(c =>
            c.WorkspaceId == ctx.WorkspaceId && c.Kind == WorkspaceFeatureContextKind.Feature));
        Assert.False(await db.WorkspaceFeatureRepositories.AnyAsync());
    }

    [Fact]
    public async Task Create_builds_Pending_worktree_paths_from_storage_root()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        ctx.AgentBridge.Respond(AgentHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "develop",
            },
        });
        // Echo success without overriding WorktreePath so the intent-built path stays on the row.
        ctx.AgentBridge.Respond(AgentHubMethods.CreateGitWorktree, new { success = true });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(
            ctx.WorkspaceId,
            "feature/path-shape",
            WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.True(result.Success, result.Error);

        const string expected =
            @"C:\Users\test\.graymoon\test-ws\features\feature\path-shape\graymoon-api";

        var createCall = Assert.Single(ctx.AgentBridge.Calls, c => c.Command == AgentHubMethods.CreateGitWorktree);
        var args = System.Text.Json.JsonSerializer.SerializeToElement(createCall.Args);
        Assert.Equal(expected, args.GetProperty("worktreePath").GetString());

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.WorkspaceFeatureRepositories
            .AsNoTracking()
            .SingleAsync(r => r.WorkspaceFeatureContextId == result.ContextId!.Value.Value);
        Assert.Equal(expected, row.WorktreePath);
    }

    [Fact]
    public async Task Exception_after_transaction_commit_marks_Feature_NeedsRepair_instead_of_stuck_Creating()
    {
        // Feature + context + Pending rows commit fine (same as the happy path); the per-repository
        // CreateGitWorktree write that follows then throws, simulating e.g. the user pressing the
        // overlay's Abort button mid Create (or any other unhandled exception) after that DB state
        // already exists. The Feature must come back as NeedsRepair - not left stuck at Creating with
        // no way for the Feature selector to open or remove it (CanSelect/ShowRemoveAction both key off
        // LifecycleState).
        await using var ctx = await SyncStateTestContext.CreateAsync(configureDb: o =>
            o.AddInterceptors(new ThrowOnFeatureRepositoryModifyInterceptor()));

        ctx.AgentBridge.Respond(AgentHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["graymoon-api"] = "develop",
            },
        });
        ctx.AgentBridge.Respond(AgentHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:\wt" });

        await using var scope = ctx.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
        var result = await ops.CreateFeatureAsync(
            ctx.WorkspaceId,
            "feature/aborted-create",
            WorkspaceFeatureBaseKindApplication.CurrentWorkspace);

        Assert.False(result.Success);
        Assert.Equal("NeedsRepair", result.Condition);
        Assert.NotNull(result.ContextId);

        await using var read = ctx.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var feature = await db.WorkspaceFeatures
            .AsNoTracking()
            .SingleAsync(f => f.WorkspaceId == ctx.WorkspaceId && f.Name == "feature/aborted-create");
        Assert.Equal(WorkspaceFeatureLifecycleState.NeedsRepair, feature.LifecycleState);
        Assert.False(string.IsNullOrWhiteSpace(feature.LastError));
    }

    /// <summary>Fails the SaveChanges that inserts Pending Feature repository rows (inside the C1 transaction).</summary>
    private sealed class ThrowOnFeatureRepositoryInsertInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            ThrowIfAddingFeatureRepositories(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfAddingFeatureRepositories(eventData.Context);
            return ValueTask.FromResult(result);
        }

        private static void ThrowIfAddingFeatureRepositories(DbContext? db)
        {
            if (db?.ChangeTracker.Entries<WorkspaceFeatureRepository>()
                    .Any(e => e.State == EntityState.Added) == true)
            {
                throw new InvalidOperationException("Simulated save failure.");
            }
        }
    }

    /// <summary>
    /// Fails the SaveChanges that writes a Feature repository row's post-worktree-creation state
    /// (Ready/NeedsRepair) - i.e. after the C1 transaction already committed Feature + context + Pending
    /// rows, simulating an exception (such as a cancelled Abort) partway through Create.
    /// </summary>
    private sealed class ThrowOnFeatureRepositoryModifyInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            ThrowIfModifyingFeatureRepositories(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfModifyingFeatureRepositories(eventData.Context);
            return ValueTask.FromResult(result);
        }

        private static void ThrowIfModifyingFeatureRepositories(DbContext? db)
        {
            if (db?.ChangeTracker.Entries<WorkspaceFeatureRepository>()
                    .Any(e => e.State == EntityState.Modified) == true)
            {
                throw new InvalidOperationException("Simulated abort mid worktree creation.");
            }
        }
    }
}
