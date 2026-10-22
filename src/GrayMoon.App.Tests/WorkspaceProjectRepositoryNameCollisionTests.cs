using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>
/// Projects are identified by their physical path, never by name: same-named projects of one repository all persist,
/// dependents resolve by path, and a package id published by several projects never produces a guessed edge.
/// </summary>
public sealed class WorkspaceProjectRepositoryNameCollisionTests
{
    private const string ShellPath = "Project.csproj";
    private const string MigrationsPath = "packages/migrations/migrations.csproj";
    private const string DbContextPath = "packages/dbcontext/dbcontext.csproj";

    private static SyncProjectInfo Shell(string path = ShellPath, params SyncPackageReference[] refs) =>
        new("Project", ProjectType.Library, path, "net10.0", null, refs);

    private static SyncProjectInfo Package(string path, string packageId, string name = "Project", params SyncPackageReference[] refs) =>
        new(name, ProjectType.Package, path, "net10.0", packageId, refs);

    private static SyncProjectInfo[] ReportedProducerRepo() =>
    [
        Shell(),
        Package(MigrationsPath, "Project", "Project"),
        Package(DbContextPath, "Persistence.Project", "Project"),
    ];

    private static SyncProjectInfo Consumer(string version = "1.2.3", string package = "Project") =>
        new("Consumer", ProjectType.Library, GeneratedPackageTestContext.ConsumerProjectFilePath, "net10.0", null,
            [new SyncPackageReference(package, version)]);

    private static async Task MergeAllAsync(WorkspaceProjectRepository repo, GeneratedPackageTestContext ctx,
        IReadOnlyList<SyncProjectInfo> producer, IReadOnlyList<SyncProjectInfo> consumer)
    {
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, producer);
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ConsumerRepositoryId, consumer);
        await repo.MergeWorkspaceProjectDependenciesAsync(ctx.WorkspaceId,
            [(ctx.ProducerRepositoryId, (IReadOnlyList<SyncProjectInfo>?)producer), (ctx.ConsumerRepositoryId, consumer)]);
    }

    [Fact]
    public async Task Reported_case_persists_all_three_producers_and_the_consumer_maps_to_migrations()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await MergeAllAsync(repo, ctx, ReportedProducerRepo(), [Consumer()]);

        var rows = await db.WorkspaceProjects.Where(p => p.RepositoryId == ctx.ProducerRepositoryId).OrderBy(p => p.ProjectFilePath).ToListAsync();
        Assert.Equal(new[] { MigrationsPath, DbContextPath, ShellPath }.Order(StringComparer.Ordinal), rows.Select(r => r.ProjectFilePath).Order(StringComparer.Ordinal));
        Assert.Null(rows.Single(r => r.ProjectFilePath == ShellPath).PackageId);

        var migrations = rows.Single(r => r.ProjectFilePath == MigrationsPath);
        var edge = await db.ProjectDependencies.SingleAsync();
        Assert.Equal(ctx.ConsumerProjectId, edge.DependentProjectId);
        Assert.Equal(migrations.ProjectId, edge.ReferencedProjectId);
        Assert.Equal("1.2.3", edge.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Result_does_not_depend_on_discovery_order(bool reversed)
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        var producer = ReportedProducerRepo();
        if (reversed) Array.Reverse(producer);
        await MergeAllAsync(repo, ctx, producer, [Consumer()]);

        Assert.Equal(3, await db.WorkspaceProjects.CountAsync(p => p.RepositoryId == ctx.ProducerRepositoryId));
        var edge = await db.ProjectDependencies.SingleAsync();
        var target = await db.WorkspaceProjects.SingleAsync(p => p.ProjectId == edge.ReferencedProjectId);
        Assert.Equal(MigrationsPath, target.ProjectFilePath);
    }

    [Fact]
    public async Task Previously_stored_shell_keeps_its_id_and_gains_the_package_rows()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        // State left by the old name-keyed merge: only the shell was stored.
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, [Shell()]);
        var shellId = (await db.WorkspaceProjects.SingleAsync(p => p.RepositoryId == ctx.ProducerRepositoryId)).ProjectId;

        for (var i = 0; i < 2; i++)
            await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, ReportedProducerRepo());

        var rows = await db.WorkspaceProjects.Where(p => p.RepositoryId == ctx.ProducerRepositoryId).ToListAsync();
        Assert.Equal(3, rows.Count);
        var shell = rows.Single(r => r.ProjectFilePath == ShellPath);
        Assert.Equal(shellId, shell.ProjectId);
        Assert.Null(shell.PackageId);
    }

    [Fact]
    public async Task Hotfixed_migrations_row_stays_bound_to_migrations_when_the_shell_is_added()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, [Package(MigrationsPath, "Project")]);
        var migrationsId = (await db.WorkspaceProjects.SingleAsync(p => p.RepositoryId == ctx.ProducerRepositoryId)).ProjectId;

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, ReportedProducerRepo());

        var migrations = await db.WorkspaceProjects.SingleAsync(p => p.ProjectFilePath == MigrationsPath);
        Assert.Equal(migrationsId, migrations.ProjectId);
        Assert.Equal("Project", migrations.PackageId);
    }

    [Fact]
    public async Task Same_name_nonpackable_projects_coexist()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();

        await ctx.Repo(scope).MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId,
            [Shell("a/App.csproj"), Shell("b/App.csproj"), Shell("c/App.csproj")]);

        var rows = await ctx.Db(scope).WorkspaceProjects.Where(p => p.RepositoryId == ctx.ProducerRepositoryId).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("Project", r.ProjectName));
    }

    [Fact]
    public async Task Same_named_consumers_keep_their_own_references_by_path()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        var producer = new[] { Package(MigrationsPath, "Pkg.A", "A"), Package(DbContextPath, "Pkg.B", "B") };
        IReadOnlyList<SyncProjectInfo> consumers =
        [
            new("App", ProjectType.Library, "one/App.csproj", "net10.0", null, [new SyncPackageReference("Pkg.A", "1.0.0")]),
            new("App", ProjectType.Library, "two/App.csproj", "net10.0", null, [new SyncPackageReference("Pkg.B", "2.0.0")]),
        ];
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, producer);
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ConsumerRepositoryId, consumers);
        await repo.MergeWorkspaceProjectDependenciesAsync(ctx.WorkspaceId, [(ctx.ConsumerRepositoryId, (IReadOnlyList<SyncProjectInfo>?)consumers)]);

        var one = await db.WorkspaceProjects.SingleAsync(p => p.ProjectFilePath == "one/App.csproj");
        var two = await db.WorkspaceProjects.SingleAsync(p => p.ProjectFilePath == "two/App.csproj");
        var edges = await db.ProjectDependencies.Where(d => d.DependentProjectId == one.ProjectId || d.DependentProjectId == two.ProjectId).ToListAsync();
        Assert.Equal(2, edges.Count);
        var a = await db.WorkspaceProjects.SingleAsync(p => p.PackageId == "Pkg.A");
        var b = await db.WorkspaceProjects.SingleAsync(p => p.PackageId == "Pkg.B");
        Assert.Equal(a.ProjectId, edges.Single(e => e.DependentProjectId == one.ProjectId).ReferencedProjectId);
        Assert.Equal(b.ProjectId, edges.Single(e => e.DependentProjectId == two.ProjectId).ReferencedProjectId);
    }

    [Fact]
    public async Task Duplicate_package_id_persists_both_producers_and_creates_no_edge()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        await MergeAllAsync(ctx.Repo(scope), ctx,
            [Package("x/X.csproj", "Dup.Pkg", "X"), Package("y/Y.csproj", "Dup.Pkg", "Y")],
            [Consumer(package: "Dup.Pkg")]);

        Assert.Equal(2, await db.WorkspaceProjects.CountAsync(p => p.PackageId == "Dup.Pkg"));
        Assert.Equal(0, await db.ProjectDependencies.CountAsync());
    }

    [Fact]
    public async Task A_second_producer_of_a_package_removes_the_edge_of_an_unscanned_consumer()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await MergeAllAsync(repo, ctx, ReportedProducerRepo(), [Consumer()]);
        Assert.Equal(1, await db.ProjectDependencies.CountAsync());

        // Only the producer repository is rescanned; it now has a second producer of "Project".
        IReadOnlyList<SyncProjectInfo> producer = [.. ReportedProducerRepo(), Package("other/Other.csproj", "Project", "Other")];
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, producer);
        await repo.MergeWorkspaceProjectDependenciesAsync(ctx.WorkspaceId, [(ctx.ProducerRepositoryId, producer)]);

        Assert.Equal(0, await db.ProjectDependencies.CountAsync());
    }

    [Fact]
    public async Task Package_becoming_nonpackable_keeps_its_id_and_loses_its_edges_even_when_the_consumer_is_not_rescanned()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await MergeAllAsync(repo, ctx, ReportedProducerRepo(), [Consumer()]);
        var migrationsId = (await db.WorkspaceProjects.SingleAsync(p => p.ProjectFilePath == MigrationsPath)).ProjectId;

        IReadOnlyList<SyncProjectInfo> producer =
            [Shell(), Shell(MigrationsPath), Package(DbContextPath, "Persistence.Project", "Project")];
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, producer);
        await repo.MergeWorkspaceProjectDependenciesAsync(ctx.WorkspaceId, [(ctx.ProducerRepositoryId, producer)]);

        var migrations = await db.WorkspaceProjects.AsNoTracking().SingleAsync(p => p.ProjectFilePath == MigrationsPath);
        Assert.Equal(migrationsId, migrations.ProjectId);
        Assert.Null(migrations.PackageId);
        Assert.Equal(ProjectType.Library, migrations.ProjectType);
        Assert.Equal(0, await db.ProjectDependencies.CountAsync());
    }

    [Fact]
    public async Task Package_id_rename_at_the_same_path_keeps_the_row_and_moves_the_edge_on_rescan()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await MergeAllAsync(repo, ctx, [Package(MigrationsPath, "Old.Id", "Lib")], [Consumer(package: "Old.Id")]);
        var libId = (await db.WorkspaceProjects.SingleAsync(p => p.ProjectFilePath == MigrationsPath)).ProjectId;

        await MergeAllAsync(repo, ctx, [Package(MigrationsPath, "New.Id", "Lib")], [Consumer(package: "New.Id")]);

        var lib = await db.WorkspaceProjects.AsNoTracking().SingleAsync(p => p.ProjectFilePath == MigrationsPath);
        Assert.Equal(libId, lib.ProjectId);
        Assert.Equal("New.Id", lib.PackageId);
        Assert.Equal(libId, (await db.ProjectDependencies.SingleAsync()).ReferencedProjectId);
    }

    [Fact]
    public async Task Moved_project_is_a_removal_plus_an_addition_never_matched_by_name()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, [Shell("old/App.csproj")]);
        var oldId = (await db.WorkspaceProjects.SingleAsync(p => p.RepositoryId == ctx.ProducerRepositoryId)).ProjectId;

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, [Shell("new/App.csproj")]);

        var row = await db.WorkspaceProjects.AsNoTracking().SingleAsync(p => p.RepositoryId == ctx.ProducerRepositoryId);
        Assert.Equal("new/App.csproj", row.ProjectFilePath);
        Assert.NotEqual(oldId, row.ProjectId);
    }

    [Fact]
    public async Task Path_case_and_separator_changes_keep_the_same_row()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, [Shell("Src/App/App.csproj")]);
        var id = (await db.WorkspaceProjects.SingleAsync(p => p.RepositoryId == ctx.ProducerRepositoryId)).ProjectId;

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, [Shell("src\\app\\App.csproj")]);

        var row = await db.WorkspaceProjects.AsNoTracking().SingleAsync(p => p.RepositoryId == ctx.ProducerRepositoryId);
        Assert.Equal(id, row.ProjectId);
        Assert.Equal("src\\app\\App.csproj", row.ProjectFilePath);
    }

    [Fact]
    public async Task Conflicting_entries_for_one_path_keep_the_first_and_identical_entries_collapse()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();

        await ctx.Repo(scope).MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId,
        [
            Shell("a/A.csproj"), Shell("a\\A.csproj"),
            new SyncProjectInfo("First", ProjectType.Library, "b/B.csproj", "net10.0", null, []),
            new SyncProjectInfo("Second", ProjectType.Library, "B/b.csproj", "net10.0", null, []),
        ]);

        var rows = await ctx.Db(scope).WorkspaceProjects.Where(p => p.RepositoryId == ctx.ProducerRepositoryId).OrderBy(p => p.ProjectFilePath).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("First", rows[1].ProjectName);
    }

    [Fact]
    public async Task Repeated_identical_scan_changes_nothing()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await MergeAllAsync(repo, ctx, ReportedProducerRepo(), [Consumer()]);
        var before = await db.WorkspaceProjects.AsNoTracking().OrderBy(p => p.ProjectId).Select(p => new { p.ProjectId, p.ProjectIdentityKey, p.PackageId }).ToListAsync();

        await MergeAllAsync(repo, ctx, ReportedProducerRepo(), [Consumer()]);

        var after = await db.WorkspaceProjects.AsNoTracking().OrderBy(p => p.ProjectId).Select(p => new { p.ProjectId, p.ProjectIdentityKey, p.PackageId }).ToListAsync();
        Assert.Equal(before, after);
        Assert.Equal(1, await db.ProjectDependencies.CountAsync());
    }

    [Fact]
    public async Task Authoritative_empty_scan_removes_physical_rows_but_not_generated_ones()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, ReportedProducerRepo());
        await repo.SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId,
            [new GeneratedPackageDependencyInfo(ctx.ConsumerRepositoryId, GeneratedPackageTestContext.ConsumerProjectFilePath, ctx.ProducerRepositoryId, "Gen.Pkg", "1.0.0")]);

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, []);

        var rows = await db.WorkspaceProjects.Where(p => p.RepositoryId == ctx.ProducerRepositoryId).ToListAsync();
        var generated = Assert.Single(rows);
        Assert.True(generated.IsGenerated);
        Assert.Equal("Gen.Pkg", generated.PackageId);
    }

    [Fact]
    public async Task A_physical_producer_takes_precedence_over_a_generated_row_of_the_same_package_id()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await repo.SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId,
            [new GeneratedPackageDependencyInfo(ctx.ConsumerRepositoryId, GeneratedPackageTestContext.ConsumerProjectFilePath, ctx.ProducerRepositoryId, "Project", "1.0.0")]);
        await MergeAllAsync(repo, ctx, [Package(MigrationsPath, "Project", "Lib")], [Consumer()]);

        var edge = await db.ProjectDependencies.SingleAsync(d => d.DependentProjectId == ctx.ConsumerProjectId && d.Version == "1.2.3");
        var target = await db.WorkspaceProjects.SingleAsync(p => p.ProjectId == edge.ReferencedProjectId);
        Assert.False(target.IsGenerated);
        Assert.Equal(MigrationsPath, target.ProjectFilePath);
    }

    [Fact]
    public async Task Merging_one_context_never_touches_another_contexts_rows()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        var feature = new WorkspaceFeatureContext { WorkspaceId = ctx.WorkspaceId, Kind = WorkspaceFeatureContextKind.Feature, CreatedAt = DateTime.UtcNow };
        db.WorkspaceFeatureContexts.Add(feature);
        await db.SaveChangesAsync();

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, ReportedProducerRepo());
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, [Shell()], feature.WorkspaceFeatureContextId);
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, [], feature.WorkspaceFeatureContextId);

        Assert.Equal(3, await db.WorkspaceProjects.CountAsync(p => p.WorkspaceFeatureContextId == ctx.SpecialContextId && p.RepositoryId == ctx.ProducerRepositoryId));
        Assert.Equal(0, await db.WorkspaceProjects.CountAsync(p => p.WorkspaceFeatureContextId == feature.WorkspaceFeatureContextId));
    }

    [Fact]
    public async Task Noncolliding_projects_are_unchanged()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();

        await ctx.Repo(scope).MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId,
        [
            new SyncProjectInfo("A", ProjectType.Library, "a/A.csproj", "net10.0", null, []),
            new SyncProjectInfo("B", ProjectType.Package, "b/B.csproj", "net10.0", "B", [])
        ]);

        var rows = await ctx.Db(scope).WorkspaceProjects.Where(p => p.RepositoryId == ctx.ProducerRepositoryId).OrderBy(p => p.ProjectName).ToListAsync();
        Assert.Equal(["A", "B"], rows.Select(r => r.ProjectName));
        Assert.Null(rows[0].PackageId);
        Assert.Equal("B", rows[1].PackageId);
    }
}
