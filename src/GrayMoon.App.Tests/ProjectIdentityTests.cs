using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>Project identity keys: normalization, the unique identity index and the strict step 11 backfill.</summary>
public sealed class ProjectIdentityTests
{
    [Theory]
    [InlineData("src/A/A.csproj", "src/A/A.csproj")]
    [InlineData("src\\A\\A.csproj", "src/A/A.csproj")]
    [InlineData("./src//A/./A.csproj", "src/A/A.csproj")]
    [InlineData("src/B/../A/A.csproj", "src/A/A.csproj")]
    [InlineData("  src/A/A.csproj ", "src/A/A.csproj")]
    public void NormalizePath_unifies_separators_and_dot_segments(string input, string expected) =>
        Assert.Equal(expected, ProjectIdentity.NormalizePath(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/abs/A.csproj")]
    [InlineData("\\abs\\A.csproj")]
    [InlineData("C:\\repo\\A.csproj")]
    [InlineData("../A.csproj")]
    [InlineData("src/../../A.csproj")]
    [InlineData(".")]
    public void NormalizePath_rejects_empty_rooted_and_escaping_paths(string? input) =>
        Assert.Null(ProjectIdentity.NormalizePath(input));

    [Fact]
    public void Keys_ignore_case_but_normalize_only_preserves_it()
    {
        Assert.Equal("src/a/a.csproj", ProjectIdentity.PhysicalKey("Src\\A\\A.csproj"));
        Assert.Equal("Src/A/A.csproj", ProjectIdentity.NormalizePath("Src\\A\\A.csproj"));
        Assert.Equal("my.pkg", ProjectIdentity.GeneratedKey(" My.Pkg "));
        Assert.Null(ProjectIdentity.GeneratedKey(" "));
    }

    private static WorkspaceProject Physical(GeneratedPackageTestContext ctx, int repositoryId, string name, string path, int? contextId = null)
    {
        var project = new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId,
            WorkspaceFeatureContextId = contextId ?? ctx.SpecialContextId,
            RepositoryId = repositoryId,
            ProjectName = name,
            ProjectType = ProjectType.Library,
            ProjectFilePath = path,
            TargetFramework = "net10.0",
        };
        project.RefreshIdentityKey();
        return project;
    }

    private static WorkspaceProject Generated(GeneratedPackageTestContext ctx, int repositoryId, string packageId)
    {
        var project = new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId,
            WorkspaceFeatureContextId = ctx.SpecialContextId,
            RepositoryId = repositoryId,
            ProjectName = packageId,
            ProjectType = ProjectType.Package,
            ProjectFilePath = "",
            TargetFramework = "",
            PackageId = packageId,
            IsGenerated = true,
        };
        project.RefreshIdentityKey();
        return project;
    }

    [Fact]
    public async Task Same_name_at_distinct_paths_coexist_but_the_same_canonical_path_does_not()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        db.WorkspaceProjects.AddRange(
            Physical(ctx, ctx.ProducerRepositoryId, "Project", "Project.csproj"),
            Physical(ctx, ctx.ProducerRepositoryId, "Project", "packages/migrations/migrations.csproj"));
        await db.SaveChangesAsync();

        db.WorkspaceProjects.Add(Physical(ctx, ctx.ProducerRepositoryId, "Other", "Packages\\Migrations\\Migrations.csproj"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_same_path_in_another_repository_or_context_is_allowed()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        var feature = new WorkspaceFeatureContext
        {
            WorkspaceId = ctx.WorkspaceId,
            Kind = WorkspaceFeatureContextKind.Feature,
            CreatedAt = DateTime.UtcNow,
        };
        db.WorkspaceFeatureContexts.Add(feature);
        await db.SaveChangesAsync();

        db.WorkspaceProjects.AddRange(
            Physical(ctx, ctx.ProducerRepositoryId, "A", "src/A/A.csproj"),
            Physical(ctx, ctx.ConsumerRepositoryId, "A", "src/A/A.csproj"),
            Physical(ctx, ctx.ProducerRepositoryId, "A", "src/A/A.csproj", feature.WorkspaceFeatureContextId));
        await db.SaveChangesAsync();

        Assert.Equal(3, await db.WorkspaceProjects.CountAsync(p => p.ProjectIdentityKey == "src/a/a.csproj"));
    }

    [Fact]
    public async Task Generated_rows_with_empty_paths_are_unique_by_package_id_and_never_collide_with_physical_keys()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        db.WorkspaceProjects.AddRange(
            Generated(ctx, ctx.ProducerRepositoryId, "Pkg.One"),
            Generated(ctx, ctx.ProducerRepositoryId, "Pkg.Two"),
            Physical(ctx, ctx.ProducerRepositoryId, "pkg.one", "pkg.one"));
        await db.SaveChangesAsync();

        db.WorkspaceProjects.Add(Generated(ctx, ctx.ProducerRepositoryId, "PKG.ONE"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static async Task SimulateLegacyAsync(GeneratedPackageTestContext ctx, AppDbContext db)
    {
        // A pre-step-11 database: no identity column, unique (context, repo, name) index.
        await db.Database.ExecuteSqlRawAsync("""DROP INDEX "IX_WorkspaceProjects_Context_Repo_Identity" """);
        await db.Database.ExecuteSqlRawAsync("""DROP INDEX "IX_WorkspaceProjects_Context_Repo_Name" """);
        await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "WorkspaceProjects" DROP COLUMN "ProjectIdentityKey" """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX "IX_WorkspaceProjects_Context_Repo_Name"
            ON "WorkspaceProjects" ("WorkspaceFeatureContextId", "RepositoryId", "ProjectName")
            WHERE "WorkspaceFeatureContextId" IS NOT NULL
            """);
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Step_11_backfills_keys_keeps_ids_and_edges_and_is_idempotent()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        var shell = Physical(ctx, ctx.ProducerRepositoryId, "Project", "Project.csproj");
        var migrations = Physical(ctx, ctx.ProducerRepositoryId, "Persistence.Project", "packages\\Migrations\\Migrations.csproj");
        var generated = Generated(ctx, ctx.ProducerRepositoryId, "Gen.Pkg");
        db.WorkspaceProjects.AddRange(shell, migrations, generated);
        await db.SaveChangesAsync();
        db.ProjectDependencies.Add(new ProjectDependency { DependentProjectId = ctx.ConsumerProjectId, ReferencedProjectId = migrations.ProjectId, Version = "1.0.0" });
        await db.SaveChangesAsync();
        var idsBefore = await db.WorkspaceProjects.OrderBy(p => p.ProjectId).Select(p => p.ProjectId).ToListAsync();

        await SimulateLegacyAsync(ctx, db);

        var step = Migrations.StrictSteps.Single(s => s.Version == 11);
        await step.Action(db);
        await step.Action(db);

        var rows = await db.WorkspaceProjects.AsNoTracking().OrderBy(p => p.ProjectId).ToListAsync();
        Assert.Equal(idsBefore, rows.Select(p => p.ProjectId));
        Assert.Equal("project.csproj", rows.Single(p => p.ProjectId == shell.ProjectId).ProjectIdentityKey);
        Assert.Equal("packages/migrations/migrations.csproj", rows.Single(p => p.ProjectId == migrations.ProjectId).ProjectIdentityKey);
        Assert.Equal("gen.pkg", rows.Single(p => p.ProjectId == generated.ProjectId).ProjectIdentityKey);
        Assert.Equal(1, await db.ProjectDependencies.CountAsync(d => d.ReferencedProjectId == migrations.ProjectId));

        // Names may now repeat at different paths.
        db.WorkspaceProjects.Add(Physical(ctx, ctx.ProducerRepositoryId, "Project", "other/Project.csproj"));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Step_11_never_deletes_a_row_whose_identity_is_duplicated_or_underivable()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        // Legacy-shaped rows: present with NULL keys (the unique index ignores NULLs).
        var first = Physical(ctx, ctx.ProducerRepositoryId, "A", "src/A/A.csproj");
        var duplicate = Physical(ctx, ctx.ProducerRepositoryId, "B", "SRC\\A\\A.csproj");
        var rooted = Physical(ctx, ctx.ProducerRepositoryId, "C", "/abs/C.csproj");
        foreach (var p in new[] { first, duplicate, rooted })
            p.ProjectIdentityKey = null;
        db.WorkspaceProjects.AddRange(first, duplicate, rooted);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Migrations.StrictSteps.Single(s => s.Version == 11).Action(db);

        var rows = await db.WorkspaceProjects.AsNoTracking()
            .Where(p => p.ProjectId == first.ProjectId || p.ProjectId == duplicate.ProjectId || p.ProjectId == rooted.ProjectId)
            .OrderBy(p => p.ProjectId).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal("src/a/a.csproj", rows[0].ProjectIdentityKey);
        Assert.Null(rows[1].ProjectIdentityKey);
        Assert.Null(rows[2].ProjectIdentityKey);
    }
}
