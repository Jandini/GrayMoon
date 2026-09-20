using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceFeatureContextMigrationTests
{
    [Fact]
    public async Task MigrateWorkspaceFeatureContextSchemaAsync_backfills_one_Workspace_context_and_repo_state()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var connector = new Connector
        {
            ConnectorName = "gh",
            ConnectorType = ConnectorType.GitHub,
            ApiBaseUrl = "https://api.github.com",
            Status = "ok",
            IsActive = true
        };
        db.Connectors.Add(connector);
        await db.SaveChangesAsync();

        var repository = new Repository
        {
            ConnectorId = connector.ConnectorId,
            RepositoryName = "RepoA",
            Visibility = "Public",
            CloneUrl = "https://github.com/acme/RepoA.git"
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        var workspace = new Workspace { Name = "AVR", IsInSync = true, LastSyncedAt = DateTime.UtcNow };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
        {
            WorkspaceId = workspace.WorkspaceId,
            RepositoryId = repository.RepositoryId,
            BranchName = "main",
            GitVersion = "1.2.3",
            OutgoingCommits = 2,
            SyncStatus = RepoSyncStatus.InSync
        });
        await db.SaveChangesAsync();

        // Simulate pre-backfill: no contexts yet (EnsureCreated created empty Feature tables).
        Assert.Empty(await db.WorkspaceFeatureContexts.ToListAsync());

        await Migrations.MigrateWorkspaceFeatureContextSchemaAsync(db);

        var contexts = await db.WorkspaceFeatureContexts.ToListAsync();
        Assert.Single(contexts);
        Assert.Equal(WorkspaceFeatureContextKind.Workspace, contexts[0].Kind);
        Assert.Null(contexts[0].WorkspaceFeatureId);
        Assert.True(contexts[0].IsInSync);

        var states = await db.WorkspaceRepositoryContextStates.ToListAsync();
        Assert.Single(states);
        Assert.Equal(contexts[0].WorkspaceFeatureContextId, states[0].WorkspaceFeatureContextId);
        Assert.Equal("main", states[0].BranchName);
        Assert.Equal("1.2.3", states[0].GitVersion);
        Assert.Equal(2, states[0].OutgoingCommits);

        var selected = await db.WorkspaceSelectedFeatureContexts.SingleAsync();
        Assert.Equal(contexts[0].WorkspaceFeatureContextId, selected.WorkspaceFeatureContextId);

        await Migrations.MigrateWorkspaceFeatureContextSchemaAsync(db);
        Assert.Equal(1, await db.WorkspaceFeatureContexts.CountAsync());
        Assert.Equal(1, await db.WorkspaceRepositoryContextStates.CountAsync());
    }

    [Fact]
    public async Task MigrateWorkspaceFeatureContextSchemaAsync_is_idempotent_for_multiple_workspaces()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Workspaces.AddRange(
            new Workspace { Name = "one" },
            new Workspace { Name = "two" });
        await db.SaveChangesAsync();

        await Migrations.MigrateWorkspaceFeatureContextSchemaAsync(db);
        await Migrations.MigrateWorkspaceFeatureContextSchemaAsync(db);

        Assert.Equal(2, await db.WorkspaceFeatureContexts.CountAsync(c => c.Kind == WorkspaceFeatureContextKind.Workspace));
        Assert.Equal(2, await db.WorkspaceSelectedFeatureContexts.CountAsync());
    }

    [Fact]
    public async Task MigrateWorkspaceFeatureContextSchemaAsync_drops_legacy_workspace_project_unique()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var connector = new Connector
        {
            ConnectorName = "gh",
            ConnectorType = ConnectorType.GitHub,
            ApiBaseUrl = "https://api.github.com",
            Status = "ok",
            IsActive = true
        };
        db.Connectors.Add(connector);
        await db.SaveChangesAsync();

        var repository = new Repository
        {
            ConnectorId = connector.ConnectorId,
            RepositoryName = "RepoA",
            Visibility = "Public",
            CloneUrl = "https://github.com/acme/RepoA.git"
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        var workspace = new Workspace { Name = "AVR" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        await Migrations.MigrateWorkspaceFeatureContextSchemaAsync(db);

        var special = await db.WorkspaceFeatureContexts.SingleAsync(c => c.WorkspaceId == workspace.WorkspaceId);
        var feature = new WorkspaceFeature
        {
            WorkspaceId = workspace.WorkspaceId,
            Name = "feat",
            LifecycleState = WorkspaceFeatureLifecycleState.Ready,
            BaseKind = WorkspaceFeatureBaseKind.CurrentWorkspace,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.WorkspaceFeatures.Add(feature);
        await db.SaveChangesAsync();

        var featureContext = new WorkspaceFeatureContext
        {
            WorkspaceId = workspace.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            WorkspaceFeatureId = feature.WorkspaceFeatureId,
            CreatedAt = DateTime.UtcNow,
            IsInSync = true
        };
        db.WorkspaceFeatureContexts.Add(featureContext);
        await db.SaveChangesAsync();

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkspaceProjects_WorkspaceId_RepositoryId_ProjectName"
                ON "WorkspaceProjects" ("WorkspaceId", "RepositoryId", "ProjectName");
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        db.WorkspaceProjects.Add(new WorkspaceProject
        {
            WorkspaceId = workspace.WorkspaceId,
            WorkspaceFeatureContextId = special.WorkspaceFeatureContextId,
            RepositoryId = repository.RepositoryId,
            ProjectName = "Shared",
            ProjectType = ProjectType.Library,
            ProjectFilePath = "Shared.csproj",
            TargetFramework = "net10.0"
        });
        await db.SaveChangesAsync();

        db.WorkspaceProjects.Add(new WorkspaceProject
        {
            WorkspaceId = workspace.WorkspaceId,
            WorkspaceFeatureContextId = featureContext.WorkspaceFeatureContextId,
            RepositoryId = repository.RepositoryId,
            ProjectName = "Shared",
            ProjectType = ProjectType.Library,
            ProjectFilePath = "Shared.csproj",
            TargetFramework = "net10.0"
        });
        var leftoverUnique = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("UNIQUE constraint failed", leftoverUnique.InnerException?.Message ?? leftoverUnique.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();

        await Migrations.MigrateWorkspaceFeatureContextSchemaAsync(db);
        await Migrations.MigrateWorkspaceFeatureContextSchemaAsync(db);

        db.WorkspaceProjects.Add(new WorkspaceProject
        {
            WorkspaceId = workspace.WorkspaceId,
            WorkspaceFeatureContextId = featureContext.WorkspaceFeatureContextId,
            RepositoryId = repository.RepositoryId,
            ProjectName = "Shared",
            ProjectType = ProjectType.Library,
            ProjectFilePath = "Shared.csproj",
            TargetFramework = "net10.0"
        });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.WorkspaceProjects.CountAsync(p =>
            p.WorkspaceId == workspace.WorkspaceId && p.ProjectName == "Shared"));
    }
}
