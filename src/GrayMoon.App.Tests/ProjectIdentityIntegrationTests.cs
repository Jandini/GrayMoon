using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>End-to-end scenarios of the project identity engine (reported collision, twins, ambiguity, reclassification, restart).</summary>
public sealed class ProjectIdentityIntegrationTests
{
    private static SyncProjectInfo Shell(string path = "Project.csproj") => new("Project", ProjectType.Library, path, "net10.0", null, []);

    private static SyncProjectInfo Package(string name, string path, string packageId) => new(name, ProjectType.Package, path, "net10.0", packageId, []);

    private static SyncProjectInfo Consumer(string path, string package, string version) =>
        new("Consumer", ProjectType.Library, path, "net10.0", null, [new SyncPackageReference(package, version)]);

    private static IReadOnlyList<SyncProjectInfo> ReportedProducer() =>
        [Shell(), Package("Project", "migrations/Project.csproj", "Project"), Package("Project", "dbcontext/Project.csproj", "Persistence.Project")];

    private static Task ReconcileAsync(GeneratedPackageTestContext ctx, GrayMoon.App.Repositories.WorkspaceProjectRepository repo,
        params ProjectScanOutcome[] outcomes) =>
        repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId, outcomes, mergeEdges: true, persistDependencyLevel: true);

    [Fact]
    public async Task Reported_collision_persists_every_project_gets_the_edge_and_updates_the_stale_version()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await ReconcileAsync(ctx, repo,
            ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, ReportedProducer()),
            ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, [Consumer("app/App.csproj", "Project", "0.9.0")]));

        Assert.Equal(3, await db.WorkspaceProjects.CountAsync(p => p.RepositoryId == ctx.ProducerRepositoryId));
        var edge = await db.ProjectDependencies.Include(d => d.ReferencedProject).SingleAsync();
        Assert.Equal("migrations/Project.csproj", edge.ReferencedProject!.ProjectFilePath);
        Assert.Equal("0.9.0", edge.Version);

        // The dependency-sync step records the new version on that edge only; it never touches project files.
        await repo.UpdateProjectDependencyVersionsAsync(ctx.WorkspaceId, [(ctx.ConsumerRepositoryId, "app/App.csproj", "Project", "1.0.0")]);
        db.ChangeTracker.Clear();
        Assert.Equal("1.0.0", (await db.ProjectDependencies.SingleAsync()).Version);
        Assert.Equal(0, await db.ProjectDependencies.CountAsync(d => d.ReferencedProject!.PackageId == "Persistence.Project"));
    }

    [Fact]
    public async Task Twin_repositories_with_the_same_structure_are_ambiguous_across_repositories_and_never_guessed()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        // The consumer repository also hosts a twin of the producer structure (central package management twin).
        var twin = ReportedProducer();
        await ReconcileAsync(ctx, repo,
            ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, ReportedProducer()),
            ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, [.. twin, Consumer("app/App.csproj", "Project", "1.0.0")]));

        Assert.Equal(2, await db.WorkspaceProjects.CountAsync(p => p.PackageId == "Project" && !p.IsGenerated));
        Assert.Equal(0, await db.ProjectDependencies.CountAsync());
    }

    [Fact]
    public async Task Same_named_consumers_resolve_independently_across_repositories()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await ReconcileAsync(ctx, repo,
            ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, [Package("A", "a/A.csproj", "Pkg.A"), Package("B", "b/B.csproj", "Pkg.B")]),
            ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, [Consumer("one/Consumer.csproj", "Pkg.A", "1.0.0"), Consumer("two/Consumer.csproj", "Pkg.B", "2.0.0")]));

        var edges = await db.ProjectDependencies.Include(d => d.DependentProject).Include(d => d.ReferencedProject).ToListAsync();
        Assert.Equal(2, edges.Count);
        Assert.Equal("a/A.csproj", edges.Single(e => e.DependentProject!.ProjectFilePath == "one/Consumer.csproj").ReferencedProject!.ProjectFilePath);
        Assert.Equal("b/B.csproj", edges.Single(e => e.DependentProject!.ProjectFilePath == "two/Consumer.csproj").ReferencedProject!.ProjectFilePath);
    }

    [Fact]
    public async Task Reclassification_and_rename_re_evaluate_consumers_and_survive_a_restart()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        int libId;
        await using (var scope = ctx.CreateScope())
        {
            var repo = ctx.Repo(scope);
            await ReconcileAsync(ctx, repo,
                ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, [Package("Lib", "lib/Lib.csproj", "Old.Id")]),
                ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, [Consumer("app/App.csproj", "Old.Id", "1.0.0")]));
            libId = (await ctx.Db(scope).WorkspaceProjects.SingleAsync(p => p.ProjectFilePath == "lib/Lib.csproj")).ProjectId;
        }

        // Rename the package id; the consumer is rescanned and still points at the old id: no edge.
        await using (var scope = ctx.CreateScope())
        {
            await ReconcileAsync(ctx, ctx.Repo(scope),
                ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, [Package("Lib", "lib/Lib.csproj", "New.Id")]),
                ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, [Consumer("app/App.csproj", "Old.Id", "1.0.0")]));
            Assert.Equal(0, await ctx.Db(scope).ProjectDependencies.CountAsync());
        }

        // Consumer follows the rename; later the package is reclassified as not packable and loses the edge again.
        await using (var scope = ctx.CreateScope())
        {
            await ReconcileAsync(ctx, ctx.Repo(scope),
                ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, [Consumer("app/App.csproj", "New.Id", "2.0.0")]));
            Assert.Equal(libId, (await ctx.Db(scope).ProjectDependencies.SingleAsync()).ReferencedProjectId);
        }
        await using (var scope = ctx.CreateScope())
        {
            await ReconcileAsync(ctx, ctx.Repo(scope),
                ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, [Shell("lib/Lib.csproj")]));
            var db = ctx.Db(scope);
            Assert.Equal(0, await db.ProjectDependencies.CountAsync());
            var lib = await db.WorkspaceProjects.SingleAsync(p => p.ProjectFilePath == "lib/Lib.csproj");
            Assert.Equal(libId, lib.ProjectId);
            Assert.Null(lib.PackageId);
        }
    }

    [Fact]
    public async Task Generated_package_row_with_the_same_id_as_a_physical_producer_never_causes_a_guess()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await repo.SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId, [ctx.MakeInfo(version: "1.0.0")]);
        await ReconcileAsync(ctx, repo,
            ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, [Package("Gen", "gen/Gen.csproj", GeneratedPackageTestContext.PackageName)]),
            ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, [Consumer(GeneratedPackageTestContext.ConsumerProjectFilePath, GeneratedPackageTestContext.PackageName, "1.0.0")]));

        var edge = await db.ProjectDependencies.Include(d => d.ReferencedProject).SingleAsync(d => d.DependentProjectId == ctx.ConsumerProjectId && d.ReferencedProject!.PackageId == GeneratedPackageTestContext.PackageName && !d.ReferencedProject.IsGenerated);
        Assert.Equal("gen/Gen.csproj", edge.ReferencedProject!.ProjectFilePath);
    }

    [Fact]
    public async Task Registry_package_lookup_ignores_projects_without_a_package_id_even_when_the_name_matches()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        await ReconcileAsync(ctx, repo, ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, ReportedProducer()));

        var found = await repo.GetPackagesByWorkspaceIdAndPackageIdsAsync(ctx.WorkspaceId, new HashSet<string> { "Project" });
        var package = Assert.Single(found);
        Assert.Equal("migrations/Project.csproj", package.ProjectFilePath);
    }
}
