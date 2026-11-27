using System.Net;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Versions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>
/// A stored effective version decision was reused by the refresh after every state write whenever the commit was
/// the same and the repository still had "packages to verify". The commit is not everything the decision was made
/// from: a second package that a consumer starts to require was never verified, a package mapped to another
/// registry was not asked there, and a branch re-parented at the same commit kept the old parent's version. The
/// decision is now stored with a key of those inputs and made again when the key differs.
/// </summary>
public sealed class EffectiveVersionEvidenceKeyTests
{
    [Fact]
    public async Task A_package_that_a_consumer_starts_to_require_is_verified_although_the_commit_is_the_same()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.ResolveAsync(bypassCache: true);
        Assert.Equal(EffectiveVersionKind.InheritedVerified, (await s.ResolveAsync(bypassCache: false)).Decisions.Single(d => d.RepositoryId == s.ProducerId).Kind);

        // A second package of the same repository, not published at the parent version, and now referenced.
        s.Registry.Answers(EffectiveVersionScenario.SecondPackageId, HttpStatusCode.NotFound);
        await s.WithDbAsync(async db =>
        {
            var second = new WorkspaceProject
            {
                WorkspaceId = s.Ctx.WorkspaceId,
                WorkspaceFeatureContextId = s.Special.Value,
                RepositoryId = s.ProducerId,
                ProjectName = "Acme.Api.Client",
                PackageId = EffectiveVersionScenario.SecondPackageId,
                ProjectType = ProjectType.Library,
                ProjectFilePath = "src/Client/Acme.Api.Client.csproj",
                TargetFramework = "net10.0",
                MatchedConnectorId = s.ConnectorId,
            };
            db.WorkspaceProjects.Add(second);
            await db.SaveChangesAsync();
            db.ProjectDependencies.Add(new ProjectDependency { DependentProjectId = s.ConsumerProjectId, ReferencedProjectId = second.ProjectId, Version = EffectiveVersionScenario.ParentVersion });
            await db.SaveChangesAsync();
        });
        var branchPointCalls = s.BranchPointCalls;

        var refreshed = await s.ResolveAsync(bypassCache: false);

        var producer = refreshed.Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.Own, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.PackageNotPublished, producer.ReasonCode);
        // The parent version of that branch at that commit is still the stored one: GitVersion does not run again.
        Assert.Equal(branchPointCalls, s.BranchPointCalls);
    }

    [Fact]
    public async Task A_package_mapped_to_another_registry_is_asked_there()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.ResolveAsync(bypassCache: true);
        await s.ResolveAsync(bypassCache: false);
        var lookups = s.Registry.Lookups;
        await s.WithDbAsync(async db =>
        {
            var other = new Connector
            {
                ConnectorName = "nuget-other",
                ConnectorType = ConnectorType.NuGet,
                ApiBaseUrl = EffectiveVersionScenario.NuGetIndexUrl,
                IsActive = true,
                IsHealthy = true,
            };
            db.Connectors.Add(other);
            await db.SaveChangesAsync();
            var producerProject = await db.WorkspaceProjects.SingleAsync(p => p.ProjectId == s.ProducerProjectId);
            producerProject.MatchedConnectorId = other.ConnectorId;
            await db.SaveChangesAsync();
        });

        // Past the short cache of registry answers, so that only a reused decision explains a missing lookup.
        s.Clock.Advance(TimeSpan.FromHours(1));

        await s.ResolveAsync(bypassCache: false);

        Assert.True(s.Registry.Lookups > lookups, "The decision made for the first registry was reused for the second.");
    }

    [Fact]
    public async Task A_branch_given_another_parent_at_the_same_commit_gets_that_parents_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.ResolveAsync(bypassCache: true);
        var branchPointCalls = s.BranchPointCalls;
        await s.WithDbAsync(async db =>
        {
            var baseline = await db.WorkspaceRepositoryBranchBaselines.SingleAsync(b => b.WorkspaceRepositoryId == s.ProducerLinkId);
            baseline.ParentBranchName = "release/3.0";
            await db.SaveChangesAsync();
        });
        s.RespondBranchPoint(success: true, version: "3.0.0-beta.1", onRemoteParent: true);
        s.Registry.Lists(EffectiveVersionScenario.PackageId, "3.0.0-beta.1");

        // Also the start of an operation, which otherwise reuses the stored parent version of the commit.
        var resolution = await s.ResolveAsync(bypassCache: true);

        var producer = resolution.Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.InheritedVerified, producer.Kind);
        Assert.Equal("3.0.0-beta.1", producer.EffectiveVersion);
        Assert.True(s.BranchPointCalls > branchPointCalls);
    }

    [Fact]
    public async Task An_unchanged_decision_is_still_reused_without_the_Worker_or_the_registry_and_keeps_its_key()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(secondPackage: true);
        await s.ResolveAsync(bypassCache: true);
        var key = (await s.ReadStateAsync(s.ProducerLinkId)).EffectiveVersionEvidenceKey;
        var (branchPointCalls, lookups) = (s.BranchPointCalls, s.Registry.Lookups);

        await s.ResolveAsync(bypassCache: false);
        await s.ResolveAsync(bypassCache: false);

        Assert.NotNull(key);
        Assert.Equal(key, (await s.ReadStateAsync(s.ProducerLinkId)).EffectiveVersionEvidenceKey);
        Assert.Equal((branchPointCalls, lookups), (s.BranchPointCalls, s.Registry.Lookups));
    }

    [Fact]
    public async Task A_decision_stored_before_the_key_existed_is_checked_once_and_then_reused()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.ResolveAsync(bypassCache: true);
        await s.MutateStateAsync(s.ProducerLinkId, state => state.EffectiveVersionEvidenceKey = null);
        var (branchPointCalls, lookups) = (s.BranchPointCalls, s.Registry.Lookups);
        // Past the short cache of registry answers, so a lookup is a real one.
        s.Clock.Advance(TimeSpan.FromHours(1));

        await s.ResolveAsync(bypassCache: false);
        var afterFirst = s.Registry.Lookups;
        await s.ResolveAsync(bypassCache: false);

        Assert.True(afterFirst > lookups);
        Assert.Equal(afterFirst, s.Registry.Lookups);
        // The parent version it recorded for this commit is kept: no GitVersion run for every repository after an upgrade.
        Assert.Equal(branchPointCalls, s.BranchPointCalls);
        Assert.NotNull((await s.ReadStateAsync(s.ProducerLinkId)).EffectiveVersionEvidenceKey);
    }

    [Fact]
    public void The_key_does_not_depend_on_the_order_or_the_case_of_the_packages_and_does_depend_on_every_input()
    {
        var baseline = new BranchBaseline("release/2.4", "ABCDEF");
        var key = EffectiveVersionResolver.EvidenceKeyOf(baseline, [new RequiredPackage("Acme.Api", 1), new RequiredPackage("Acme.Client", 2)]);

        Assert.Equal(2 * EffectiveVersionResolver.BaselineKeyLength, key.Length);
        Assert.Equal(key, EffectiveVersionResolver.EvidenceKeyOf(new BranchBaseline(" release/2.4 ", "abcdef"), [new RequiredPackage("acme.client", 2), new RequiredPackage("ACME.API", 1)]));
        Assert.NotEqual(key, EffectiveVersionResolver.EvidenceKeyOf(baseline, [new RequiredPackage("Acme.Api", 1)]));
        Assert.NotEqual(key, EffectiveVersionResolver.EvidenceKeyOf(baseline, [new RequiredPackage("Acme.Api", 1), new RequiredPackage("Acme.Client", null)]));
        Assert.NotEqual(key, EffectiveVersionResolver.EvidenceKeyOf(new BranchBaseline("release/2.5", "ABCDEF"), [new RequiredPackage("Acme.Api", 1), new RequiredPackage("Acme.Client", 2)]));
        Assert.NotEqual(key, EffectiveVersionResolver.EvidenceKeyOf(new BranchBaseline("release/2.4", "ABCDE0"), [new RequiredPackage("Acme.Api", 1), new RequiredPackage("Acme.Client", 2)]));
        // The first half is the parent branch and the branch point alone.
        Assert.Equal(key[..EffectiveVersionResolver.BaselineKeyLength], EffectiveVersionResolver.EvidenceKeyOf(baseline, [])[..EffectiveVersionResolver.BaselineKeyLength]);
    }

    // ---------------------------------------------------------------- migration

    [Fact]
    public async Task A_version_23_database_gets_the_column_keeps_its_decisions_and_the_step_is_idempotent()
    {
        var (connection, db) = await CreateSeededDbAsync();
        await using var _ = connection;
        await using var __ = db;
        await ExecuteAsync(connection, "ALTER TABLE WorkspaceRepositoryContextStates DROP COLUMN EffectiveVersionEvidenceKey; PRAGMA user_version = 23;");

        var step = Migrations.StrictSteps.Single(s => s.Version == 24);
        await Migrations.RunStrictStepAsync(db, step.Version, step.Name, step.Action, null, NullLogger.Instance);
        await step.Action(db);

        Assert.Equal(24L, await ScalarAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositoryContextStates') WHERE name = 'EffectiveVersionEvidenceKey'"));
        var state = await db.WorkspaceRepositoryContextStates.AsNoTracking().SingleAsync();
        Assert.Equal(("2.4.0-beta.2", "abc", "Verified"), (state.EffectiveVersion, state.EffectiveVersionCommitSha, state.EffectiveVersionReasonCode));
        Assert.Null(state.EffectiveVersionEvidenceKey);
    }

    [Fact]
    public async Task The_runner_adds_the_column_before_an_older_step_writes_context_state_rows_through_the_model()
    {
        // Strict step 16 inserts context state rows through EF, whose model already has the column: without the
        // column in place first, an upgrade from an older database fails there with "no such column".
        var (connection, db) = await CreateSeededDbAsync();
        await using var _ = connection;
        await using var __ = db;
        await ExecuteAsync(connection, """
            DELETE FROM WorkspaceRepositoryContextStates;
            ALTER TABLE WorkspaceRepositoryContextStates DROP COLUMN EffectiveVersionEvidenceKey;
            PRAGMA user_version = 15;
            """);

        await Migrations.RunAllAsync(db, NullLogger.Instance, null, _ => Task.FromResult(true));

        Assert.Equal((long)Migrations.StrictSteps.Max(s => s.Version), await ScalarAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('WorkspaceRepositoryContextStates') WHERE name = 'EffectiveVersionEvidenceKey'"));
        // The backfill of step 16 wrote the missing state row through the model.
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM WorkspaceRepositoryContextStates"));
    }

    /// <summary>A current-schema database with one Workspace, one repository and one stored inherited decision.</summary>
    private static async Task<(SqliteConnection Connection, AppDbContext Db)> CreateSeededDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.CreateSchemaAsync();

        var connector = new Connector { ConnectorName = "gh", ConnectorType = ConnectorType.GitHub, ApiBaseUrl = "https://api.github.com", IsActive = true, IsHealthy = true };
        var workspace = new Workspace { Name = "ws" };
        db.Connectors.Add(connector);
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var repository = new Repository { ConnectorId = connector.ConnectorId, RepositoryName = "r", OrgName = "acme", CloneUrl = "https://example/r.git" };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        var link = new WorkspaceRepositoryLink { WorkspaceId = workspace.WorkspaceId, RepositoryId = repository.RepositoryId };
        var context = new WorkspaceFeatureContext { WorkspaceId = workspace.WorkspaceId, Kind = WorkspaceFeatureContextKind.Workspace, CreatedAt = DateTime.UtcNow };
        db.WorkspaceRepositories.Add(link);
        db.WorkspaceFeatureContexts.Add(context);
        await db.SaveChangesAsync();
        db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
        {
            WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
            WorkspaceRepositoryId = link.WorkspaceRepositoryId,
            HeadCommit = "abc",
            EffectiveVersion = "2.4.0-beta.2",
            EffectiveVersionKind = (int)EffectiveVersionKind.InheritedVerified,
            EffectiveVersionCommitSha = "abc",
            EffectiveVersionReasonCode = EffectiveVersionReasonCodes.Verified,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (connection, db);
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
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
