using System.Data.Common;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GrayMoon.App.Tests;

/// <summary>
/// Unit F: the Repositories header-state query is capability-aware at its outer boundary. A workspace without
/// the dependency graph runs no dependency aggregate (asserted on the SQL actually issued), never reports
/// unmatched dependencies or a level needing work - even over stale persisted values - and reports
/// out-of-date version files on their own instead. A dependency workspace (or a caller stating no profile) is
/// unchanged.
/// </summary>
public sealed class WorkspaceRepositoryLinkHeaderStateProfileTests
{
    private static readonly WorkspaceCapabilities BasicNone =
        new(WorkspaceType.Basic, WorkspaceVersioningMode.None, WorkspaceCiProvider.None);

    private static readonly WorkspaceCapabilities BasicGitVersion =
        new(WorkspaceType.Basic, WorkspaceVersioningMode.GitVersion, WorkspaceCiProvider.None);

    [Fact]
    public async Task Basic_workspace_header_ignores_dependency_state_and_reports_out_of_date_files()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(30);
        await using (ctx)
        {
            // The seed carries unmatched dependencies, levels and an out-of-date file (row 0), as stale state would.
            var recorder = new SqlRecorder();
            var query = ctx.CreateWorkspaceRepoLinkQuery(recorder);

            var header = await query.GetHeaderStateAsync(workspaceId, capabilities: BasicNone);

            Assert.Equal(30, header.TotalCount);
            Assert.False(header.HasUnmatchedDependencies);
            Assert.Null(header.LowestLevelNeedingWork);
            Assert.True(header.HasOutOfDateFiles);
            Assert.True(header.IsPushRecommended);
            Assert.DoesNotContain(recorder.Commands, sql => sql.Contains("UnmatchedDeps", StringComparison.Ordinal));
            Assert.DoesNotContain(recorder.Commands, sql => sql.Contains("DependencyLevel", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Basic_gitversion_workspace_is_treated_like_basic_for_dependency_state()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(30);
        await using (ctx)
        {
            var header = await ctx.WorkspaceRepoLinkQuery.GetHeaderStateAsync(workspaceId, capabilities: BasicGitVersion);

            Assert.False(header.HasUnmatchedDependencies);
            Assert.Null(header.LowestLevelNeedingWork);
            Assert.True(header.HasOutOfDateFiles);
        }
    }

    [Fact]
    public async Task Basic_workspace_with_up_to_date_files_reports_nothing_to_update()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(30);
        await using (ctx)
        {
            await ctx.DbContext.WorkspaceRepositories
                .Where(wr => wr.WorkspaceId == workspaceId)
                .ExecuteUpdateAsync(s => s.SetProperty(wr => wr.OutOfDateFileRepos, (int?)null));

            var header = await ctx.WorkspaceRepoLinkQuery.GetHeaderStateAsync(workspaceId, capabilities: BasicNone);

            Assert.False(header.HasOutOfDateFiles);
        }
    }

    [Fact]
    public async Task DotNet_workspace_header_is_unchanged_and_matches_the_profile_less_call()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(30);
        await using (ctx)
        {
            var recorder = new SqlRecorder();
            var query = ctx.CreateWorkspaceRepoLinkQuery(recorder);

            var dotNet = await query.GetHeaderStateAsync(workspaceId, capabilities: WorkspaceCapabilities.Legacy);
            var legacy = await ctx.WorkspaceRepoLinkQuery.GetHeaderStateAsync(workspaceId);

            Assert.Equal(legacy, dotNet);
            Assert.True(dotNet.HasUnmatchedDependencies);
            Assert.Equal(0, dotNet.LowestLevelNeedingWork);
            Assert.False(dotNet.HasOutOfDateFiles);
            Assert.Contains(recorder.Commands, sql => sql.Contains("UnmatchedDeps", StringComparison.Ordinal));
            Assert.Contains(recorder.Commands, sql => sql.Contains("DependencyLevel", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Feature_context_reads_its_own_state_and_honours_the_profile()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(3);
        await using (ctx)
        {
            var contextId = await CreateFeatureContextAsync(ctx.DbContext, workspaceId);
            var links = await ctx.DbContext.WorkspaceRepositories.AsNoTracking()
                .Where(wr => wr.WorkspaceId == workspaceId)
                .OrderBy(wr => wr.WorkspaceRepositoryId)
                .ToListAsync();

            // The shared links are clean; only the Feature's own state carries dependency and file data.
            await ctx.DbContext.WorkspaceRepositories
                .Where(wr => wr.WorkspaceId == workspaceId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(wr => wr.UnmatchedDeps, (int?)null)
                    .SetProperty(wr => wr.OutOfDateFileRepos, (int?)null));
            ctx.DbContext.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
            {
                WorkspaceFeatureContextId = contextId.Value,
                WorkspaceRepositoryId = links[0].WorkspaceRepositoryId,
                BranchName = "feature",
                UnmatchedDeps = 2,
                DependencyLevel = 3,
                OutOfDateFileRepos = 1,
                SyncStatus = RepoSyncStatus.InSync,
            });
            await ctx.DbContext.SaveChangesAsync();

            var recorder = new SqlRecorder();
            var query = ctx.CreateWorkspaceRepoLinkQuery(recorder);

            var basic = await query.GetHeaderStateAsync(workspaceId, contextId, isSpecialWorkspace: false, capabilities: BasicNone);
            Assert.False(basic.HasUnmatchedDependencies);
            Assert.Null(basic.LowestLevelNeedingWork);
            Assert.True(basic.HasOutOfDateFiles);
            Assert.DoesNotContain(recorder.Commands, sql => sql.Contains("UnmatchedDeps", StringComparison.Ordinal));
            Assert.DoesNotContain(recorder.Commands, sql => sql.Contains("DependencyLevel", StringComparison.Ordinal));

            var dotNet = await ctx.WorkspaceRepoLinkQuery.GetHeaderStateAsync(
                workspaceId, contextId, isSpecialWorkspace: false, capabilities: WorkspaceCapabilities.Legacy);
            Assert.True(dotNet.HasUnmatchedDependencies);
            Assert.Equal(3, dotNet.LowestLevelNeedingWork);
            Assert.False(dotNet.HasOutOfDateFiles);

            // The special Workspace reads the clean shared links, not the Feature's state.
            var workspaceBasic = await ctx.WorkspaceRepoLinkQuery.GetHeaderStateAsync(workspaceId, capabilities: BasicNone);
            Assert.False(workspaceBasic.HasOutOfDateFiles);
        }
    }

    private static async Task<WorkspaceFeatureContextId> CreateFeatureContextAsync(AppDbContext db, int workspaceId)
    {
        var feature = new WorkspaceFeature
        {
            WorkspaceId = workspaceId,
            Name = "feature-profile",
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = workspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true,
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();
        return new WorkspaceFeatureContextId(context.WorkspaceFeatureContextId);
    }

    /// <summary>Records the text of every command the query service sends to SQLite.</summary>
    private sealed class SqlRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
