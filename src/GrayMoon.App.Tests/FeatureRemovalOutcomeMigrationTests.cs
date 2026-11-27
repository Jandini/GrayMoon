using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// Strict step 23 adds <c>WorkspaceFeatureRepositories.RemovalOutcomeJson</c> to a database that predates it, also
/// one that was shut down in the middle of a Feature removal.
/// </summary>
public sealed class FeatureRemovalOutcomeMigrationTests
{
    [Fact]
    public async Task A_version_22_database_with_a_removal_in_progress_gets_the_column_and_keeps_its_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.CreateSchemaAsync();

        // The shipped state: a Feature whose removal stopped after one of its two repositories.
        var connector = new Connector { ConnectorName = "github", ConnectorType = ConnectorType.GitHub, ApiBaseUrl = "https://api.github.com", IsActive = true };
        db.Connectors.Add(connector);
        await db.SaveChangesAsync();
        var workspace = new Workspace { Name = "ws", RootPath = @"C:\ws" };
        db.Workspaces.Add(workspace);
        var repositories = new[] { "api", "web" }
            .Select(name => new Repository { ConnectorId = connector.ConnectorId, RepositoryName = name, OrgName = "acme", Visibility = "Public", CloneUrl = $"https://github.com/acme/{name}.git" })
            .ToList();
        db.Repositories.AddRange(repositories);
        await db.SaveChangesAsync();
        var links = repositories
            .Select(r => new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = r.RepositoryId, DefaultBranchName = "main" })
            .ToList();
        db.WorkspaceRepositories.AddRange(links);
        var feature = new WorkspaceFeature
        {
            WorkspaceId = workspace.WorkspaceId,
            Name = "feat",
            LifecycleState = WorkspaceFeatureLifecycleState.NeedsRepair,
            LastError = "Interrupted while removing",
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();
        var context = new WorkspaceFeatureContext
        {
            WorkspaceId = workspace.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();
        db.WorkspaceFeatureRepositories.AddRange(
            new WorkspaceFeatureRepository
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = links[0].WorkspaceRepositoryId,
                WorktreePath = @"C:\features\feat\api",
                BaseCommitSha = "abc123",
                State = WorkspaceFeatureRepositoryState.Removed,
                CreatedAt = DateTime.UtcNow,
            },
            new WorkspaceFeatureRepository
            {
                WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                WorkspaceRepositoryId = links[1].WorkspaceRepositoryId,
                WorktreePath = @"C:\features\feat\web",
                BaseCommitSha = "def456",
                State = WorkspaceFeatureRepositoryState.Removing,
                LastError = "web: changed since the check, run Remove again.",
                CreatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await ExecuteAsync(connection, """ALTER TABLE "WorkspaceFeatureRepositories" DROP COLUMN "RemovalOutcomeJson" """);
        await ExecuteAsync(connection, "PRAGMA user_version = 22;");

        var step = Migrations.StrictSteps.Single(s => s.Version == 23);
        await Migrations.RunStrictStepAsync(db, step.Version, step.Name, step.Action, null, NullLogger.Instance);
        // Idempotent: a brand-new database already has the column when the step runs.
        await step.Action(db);

        Assert.Equal(23L, await ScalarAsync(connection, "PRAGMA user_version;"));
        var rows = await db.WorkspaceFeatureRepositories.AsNoTracking().OrderBy(r => r.WorkspaceFeatureRepositoryId).ToListAsync();
        Assert.Equal(
            [WorkspaceFeatureRepositoryState.Removed, WorkspaceFeatureRepositoryState.Removing],
            rows.Select(r => r.State));
        Assert.Equal("web: changed since the check, run Remove again.", rows[1].LastError);
        Assert.All(rows, r => Assert.Null(r.RemovalOutcomeJson));

        // A row from before the column has no record: a retry treats every step as not run, and what it stores reads back.
        var empty = FeatureRepositoryRemovalRecord.Parse(rows[0].RemovalOutcomeJson);
        Assert.True(empty.IsUnresolved(RemoveFeatureSteps.LocalBranch));
        var tracked = await db.WorkspaceFeatureRepositories.SingleAsync(r => r.WorkspaceFeatureRepositoryId == rows[1].WorkspaceFeatureRepositoryId);
        tracked.RemovalOutcomeJson = empty
            .With(OperationStepOutcome.Blocked(RemoveFeatureSteps.Worktree, OperationReasonCodes.UncommittedChanges, "kept", OperationAuthorizations.DiscardUncommittedChanges))
            .ToJson();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var stored = FeatureRepositoryRemovalRecord.Parse(
            (await db.WorkspaceFeatureRepositories.AsNoTracking().SingleAsync(r => r.WorkspaceFeatureRepositoryId == tracked.WorkspaceFeatureRepositoryId)).RemovalOutcomeJson);
        Assert.Equal(OperationReasonCodes.UncommittedChanges, stored.Step(RemoveFeatureSteps.Worktree)!.ReasonCode);
        Assert.False(stored.WorktreeRemoved);
    }

    [Fact]
    public void A_record_that_cannot_be_read_is_an_empty_record_so_a_retry_runs_every_step()
    {
        var record = FeatureRepositoryRemovalRecord.Parse("{ not json");

        Assert.Empty(record.Steps);
        Assert.True(record.IsUnresolved(RemoveFeatureSteps.Worktree));
        Assert.False(record.WorktreeRemoved);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
