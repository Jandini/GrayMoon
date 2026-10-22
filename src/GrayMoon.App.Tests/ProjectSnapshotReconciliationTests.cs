using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GrayMoon.App.Tests;

/// <summary>ReconcileProjectSnapshotAsync is atomic, never acts on a failed scan and gives the same graph for any entry shape.</summary>
public sealed class ProjectSnapshotReconciliationTests
{
    private const string LibPath = "lib/Lib.csproj";

    private static SyncProjectInfo Lib(string package = "Lib.Pkg") => new("Lib", ProjectType.Package, LibPath, "net10.0", package, []);

    private static SyncProjectInfo App(string path = "app/App.csproj", string package = "Lib.Pkg") =>
        new("App", ProjectType.Library, path, "net10.0", null, [new SyncPackageReference(package, "1.0.0")]);

    private sealed class ThrowOnSaveInterceptor : SaveChangesInterceptor
    {
        private int _throwOnSave = int.MaxValue;
        private int _saves;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (++_saves >= _throwOnSave)
                throw new InvalidOperationException("injected save failure");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public void Arm(int savesFromNow) { _saves = 0; _throwOnSave = savesFromNow; }
        public void Disarm() => _throwOnSave = int.MaxValue;
    }

    private static async Task<List<string>> GraphAsync(AppDbContext db)
    {
        var edges = await db.ProjectDependencies.AsNoTracking()
            .Select(d => d.DependentProject!.ProjectFilePath + ">" + d.ReferencedProject!.ProjectFilePath + "@" + d.Version).ToListAsync();
        var projects = await db.WorkspaceProjects.AsNoTracking().Where(p => !p.IsGenerated).Select(p => p.RepositoryId + ":" + p.ProjectFilePath).ToListAsync();
        return edges.Concat(projects).Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task Fault_after_the_project_merge_rolls_back_projects_and_edges()
    {
        var interceptor = new ThrowOnSaveInterceptor();
        await using var ctx = await GeneratedPackageTestContext.CreateAsync(interceptor);
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId,
            [ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, [Lib()]), ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, [App()])],
            mergeEdges: true, persistDependencyLevel: true);
        var before = await GraphAsync(db);

        // The second save is the edge write: the project merge (first save) must roll back with it.
        interceptor.Arm(2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId,
            [ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, [Lib(), new("Extra", ProjectType.Library, "extra/Extra.csproj", "net10.0", null, [])]),
             ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, [App("moved/App.csproj")])],
            mergeEdges: true, persistDependencyLevel: true));
        interceptor.Disarm();

        await using var verifyScope = ctx.CreateScope();
        Assert.Equal(before, await GraphAsync(ctx.Db(verifyScope)));
    }

    [Fact]
    public async Task Failed_scans_change_nothing()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId,
            [ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, [Lib()]), ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, [App()])],
            mergeEdges: true, persistDependencyLevel: true);
        var before = await GraphAsync(db);

        await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId,
            [ProjectScanOutcome.Failed(ctx.ProducerRepositoryId), ProjectScanOutcome.FromProjectsDetail(ctx.ConsumerRepositoryId, null)],
            mergeEdges: true, persistDependencyLevel: true);

        Assert.Equal(before, await GraphAsync(db));
    }

    [Fact]
    public async Task Mixed_batch_touches_only_the_successful_repositories_and_keeps_the_graph_coherent()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId,
            [ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, [Lib()]), ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, [App()])],
            mergeEdges: true, persistDependencyLevel: true);

        // Producer scan failed; the consumer scan succeeded with a changed project path.
        await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId,
            [ProjectScanOutcome.Failed(ctx.ProducerRepositoryId), ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, [App("app2/App.csproj")])],
            mergeEdges: true, persistDependencyLevel: true);

        Assert.Equal(1, await db.WorkspaceProjects.CountAsync(p => p.RepositoryId == ctx.ProducerRepositoryId && p.ProjectFilePath == LibPath));
        Assert.Equal(["app2/App.csproj>lib/Lib.csproj@1.0.0"], (await GraphAsync(db)).Where(s => s.Contains('>')));
    }

    [Fact]
    public async Task Empty_success_clears_physical_projects_but_keeps_generated_rows()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId,
            [ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, [Lib()])], mergeEdges: true, persistDependencyLevel: true);
        await repo.SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId, [ctx.MakeInfo(version: "1.0.0")]);

        await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId,
            [ProjectScanOutcome.FromProjectsDetail(ctx.ProducerRepositoryId, [])], mergeEdges: true, persistDependencyLevel: true);

        var rows = await db.WorkspaceProjects.AsNoTracking().Where(p => p.RepositoryId == ctx.ProducerRepositoryId).ToListAsync();
        Assert.True(Assert.Single(rows).IsGenerated);
    }

    [Fact]
    public async Task One_batch_and_per_repository_calls_give_the_same_graph()
    {
        List<string> batch, single;
        await using (var ctx = await GeneratedPackageTestContext.CreateAsync())
        {
            await using var scope = ctx.CreateScope();
            await ctx.Repo(scope).ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId,
                [ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, [Lib()]), ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, [App()])],
                mergeEdges: true, persistDependencyLevel: true);
            batch = await GraphAsync(ctx.Db(scope));
        }
        await using (var ctx = await GeneratedPackageTestContext.CreateAsync())
        {
            await using var scope = ctx.CreateScope();
            var repo = ctx.Repo(scope);
            await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId,
                [ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, [Lib()])], mergeEdges: true, persistDependencyLevel: true);
            await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId,
                [ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, [App()])], mergeEdges: true, persistDependencyLevel: true);
            single = await GraphAsync(ctx.Db(scope));
        }

        Assert.Equal(batch, single);
        Assert.Contains(batch, s => s.Contains('>'));
    }

    [Fact]
    public async Task Joins_a_transaction_the_caller_already_opened()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await using (var outer = await db.Database.BeginTransactionAsync())
        {
            await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId,
                [ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, [Lib()])], mergeEdges: true, persistDependencyLevel: true);
            await outer.RollbackAsync();
        }

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.WorkspaceProjects.CountAsync(p => p.RepositoryId == ctx.ProducerRepositoryId));
    }
}
