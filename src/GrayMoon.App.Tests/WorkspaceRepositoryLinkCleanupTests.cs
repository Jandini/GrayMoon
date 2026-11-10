using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>
/// Review 2026-11-10 finding 16: unlinking a repository must also remove its Files page rows
/// (WorkspaceFiles, WorkspaceFileVersionConfigs, WorkspaceFileLineStatuses, WorkspaceFileContextStates)
/// in every context of that workspace, and nothing of another repository or another workspace.
/// </summary>
public sealed class WorkspaceRepositoryLinkCleanupTests
{
    [Fact]
    public async Task DeleteDependentsAsync_removes_file_rows_of_the_unlinked_repository_in_every_context()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        // Foreign keys off: the cleanup must delete the children itself and not rely on a cascade.
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            await pragma.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.CreateSchemaAsync();

        var connector = new Connector { ConnectorName = "github", ConnectorType = ConnectorType.GitHub, ApiBaseUrl = "https://api.github.com", IsActive = true };
        db.Connectors.Add(connector);
        await db.SaveChangesAsync();

        var unlinked = new Repository { ConnectorId = connector.ConnectorId, RepositoryName = "unlinked", OrgName = "acme", CloneUrl = "https://example/unlinked.git" };
        var other = new Repository { ConnectorId = connector.ConnectorId, RepositoryName = "other", OrgName = "acme", CloneUrl = "https://example/other.git" };
        db.Repositories.AddRange(unlinked, other);
        var workspace = new Workspace { Name = "ws" };
        var otherWorkspace = new Workspace { Name = "other-ws" };
        db.Workspaces.AddRange(workspace, otherWorkspace);
        await db.SaveChangesAsync();

        var link = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = unlinked.RepositoryId };
        db.WorkspaceRepositories.Add(link);
        var specialContext = new WorkspaceFeatureContext { WorkspaceId = workspace.WorkspaceId, Kind = WorkspaceFeatureContextKind.Workspace, CreatedAt = DateTime.UtcNow };
        var feature = new WorkspaceFeature
        {
            WorkspaceId = workspace.WorkspaceId,
            Name = "feature",
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
            CreatedAt = DateTime.UtcNow
        };
        db.WorkspaceFeatureContexts.AddRange(specialContext, featureContext);
        await db.SaveChangesAsync();

        var unlinkedFile = new WorkspaceFile { WorkspaceId = workspace.WorkspaceId, RepositoryId = unlinked.RepositoryId, FileName = "versions.env", FilePath = "deploy/versions.env" };
        var otherRepositoryFile = new WorkspaceFile { WorkspaceId = workspace.WorkspaceId, RepositoryId = other.RepositoryId, FileName = "versions.env", FilePath = "deploy/versions.env" };
        var otherWorkspaceFile = new WorkspaceFile { WorkspaceId = otherWorkspace.WorkspaceId, RepositoryId = unlinked.RepositoryId, FileName = "versions.env", FilePath = "deploy/versions.env" };
        db.WorkspaceFiles.AddRange(unlinkedFile, otherRepositoryFile, otherWorkspaceFile);
        await db.SaveChangesAsync();

        db.WorkspaceFileVersionConfigs.AddRange(
            new WorkspaceFileVersionConfig { FileId = unlinkedFile.FileId, VersionPattern = "A={other}" },
            new WorkspaceFileVersionConfig { FileId = otherRepositoryFile.FileId, VersionPattern = "A={unlinked}" },
            new WorkspaceFileVersionConfig { FileId = otherWorkspaceFile.FileId, VersionPattern = "A={other}" });
        db.WorkspaceFileContextStates.AddRange(
            new WorkspaceFileContextState { WorkspaceFeatureContextId = specialContext.WorkspaceFeatureContextId, FileId = unlinkedFile.FileId },
            new WorkspaceFileContextState { WorkspaceFeatureContextId = featureContext.WorkspaceFeatureContextId, FileId = unlinkedFile.FileId },
            new WorkspaceFileContextState { WorkspaceFeatureContextId = specialContext.WorkspaceFeatureContextId, FileId = otherRepositoryFile.FileId });
        db.WorkspaceFileLineStatuses.AddRange(
            LineStatus(workspace.WorkspaceId, specialContext.WorkspaceFeatureContextId, unlinked.RepositoryId),
            LineStatus(workspace.WorkspaceId, featureContext.WorkspaceFeatureContextId, unlinked.RepositoryId),
            LineStatus(workspace.WorkspaceId, specialContext.WorkspaceFeatureContextId, other.RepositoryId),
            LineStatus(otherWorkspace.WorkspaceId, null, unlinked.RepositoryId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await WorkspaceRepositoryLinkCleanup.DeleteDependentsAsync(
            db, [link.WorkspaceRepositoryId], [unlinked.RepositoryId], workspace.WorkspaceId);

        var remainingFileIds = await db.WorkspaceFiles.AsNoTracking().Select(f => f.FileId).OrderBy(id => id).ToListAsync();
        Assert.Equal(new[] { otherRepositoryFile.FileId, otherWorkspaceFile.FileId }.OrderBy(id => id), remainingFileIds);
        Assert.Equal(
            remainingFileIds,
            await db.WorkspaceFileVersionConfigs.AsNoTracking().Select(c => c.FileId).OrderBy(id => id).ToListAsync());
        Assert.Equal(
            [otherRepositoryFile.FileId],
            await db.WorkspaceFileContextStates.AsNoTracking().Select(s => s.FileId).ToListAsync());

        var remainingStatuses = await db.WorkspaceFileLineStatuses.AsNoTracking()
            .Select(s => new { s.WorkspaceId, s.RepositoryId })
            .ToListAsync();
        Assert.Equal(2, remainingStatuses.Count);
        Assert.DoesNotContain(remainingStatuses, s => s.WorkspaceId == workspace.WorkspaceId && s.RepositoryId == unlinked.RepositoryId);
    }

    private static WorkspaceFileLineStatus LineStatus(int workspaceId, int? contextId, int repositoryId) => new()
    {
        WorkspaceId = workspaceId,
        WorkspaceFeatureContextId = contextId,
        RepositoryId = repositoryId,
        FilePath = "deploy/versions.env",
        FileName = "versions.env",
        TokenName = "token",
        CurrentValue = "1.0.0",
        ExpectedValue = "1.1.0",
        TotalMatchedLines = 1,
        OutOfDateLines = 1
    };
}
