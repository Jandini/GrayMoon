using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>
/// Several discovered projects of one repository can share a ProjectName while only one row per name is stored.
/// The package producer must win over a same-name non-package shell, independent of discovery order.
/// </summary>
public sealed class WorkspaceProjectRepositoryNameCollisionTests
{
    private const string ShellPath = "Project.csproj";
    private const string PackagePath = "packages/migrations/Migrations.csproj";

    private static SyncProjectInfo Shell(string path = ShellPath, params SyncPackageReference[] refs) =>
        new("Project", ProjectType.Library, path, "net10.0", null, refs);

    private static SyncProjectInfo Package(string path = PackagePath, string packageId = "Project", params SyncPackageReference[] refs) =>
        new("Project", ProjectType.Package, path, "net10.0", packageId, refs);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Package_candidate_wins_regardless_of_input_order(bool shellFirst)
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        SyncProjectInfo[] incoming = shellFirst ? [Shell(), Package()] : [Package(), Shell()];
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, incoming);

        var row = await ctx.Db(scope).WorkspaceProjects.SingleAsync(p => p.RepositoryId == ctx.ProducerRepositoryId);
        Assert.Equal(ProjectType.Package, row.ProjectType);
        Assert.Equal("Project", row.PackageId);
        Assert.Equal(PackagePath, row.ProjectFilePath);
    }

    [Fact]
    public async Task Previously_persisted_shell_is_repaired_in_place_and_repeat_sync_is_idempotent()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        // Corrupted state from the old First() behaviour: only the shell was stored.
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, [Shell()]);
        var before = await db.WorkspaceProjects.SingleAsync(p => p.RepositoryId == ctx.ProducerRepositoryId);
        Assert.Null(before.PackageId);

        for (var i = 0; i < 2; i++)
            await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, [Shell(), Package()]);

        var rows = await db.WorkspaceProjects.Where(p => p.RepositoryId == ctx.ProducerRepositoryId).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(before.ProjectId, row.ProjectId);
        Assert.Equal(ProjectType.Package, row.ProjectType);
        Assert.Equal("Project", row.PackageId);
        Assert.Equal(PackagePath, row.ProjectFilePath);
    }

    [Fact]
    public async Task Repaired_producer_gets_a_consumer_edge_and_level_on_the_next_complete_sync()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);
        var consumer = await db.WorkspaceProjects.SingleAsync(p => p.ProjectId == ctx.ConsumerProjectId);
        var consumerInfo = new SyncProjectInfo(consumer.ProjectName, ProjectType.Library, consumer.ProjectFilePath, "net10.0", null,
            [new SyncPackageReference("Project", "1.2.3")]);

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, [Shell()]);
        await repo.MergeWorkspaceProjectDependenciesAsync(ctx.WorkspaceId,
            [(ctx.ConsumerRepositoryId, (IReadOnlyList<SyncProjectInfo>?)[consumerInfo]), (ctx.ProducerRepositoryId, [Shell()])]);
        Assert.Equal(0, await db.ProjectDependencies.CountAsync());

        IReadOnlyList<SyncProjectInfo> producerNow = [Shell(), Package()];
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, producerNow);
        await repo.MergeWorkspaceProjectDependenciesAsync(ctx.WorkspaceId,
            [(ctx.ConsumerRepositoryId, (IReadOnlyList<SyncProjectInfo>?)[consumerInfo]), (ctx.ProducerRepositoryId, producerNow)]);

        var producerRow = await db.WorkspaceProjects.SingleAsync(p => p.RepositoryId == ctx.ProducerRepositoryId);
        var edge = await db.ProjectDependencies.SingleAsync();
        Assert.Equal(ctx.ConsumerProjectId, edge.DependentProjectId);
        Assert.Equal(producerRow.ProjectId, edge.ReferencedProjectId);
        Assert.Equal("1.2.3", edge.Version);

        var links = await db.WorkspaceRepositories.Where(wr => wr.WorkspaceId == ctx.WorkspaceId).ToListAsync();
        Assert.True(links.Single(l => l.RepositoryId == ctx.ConsumerRepositoryId).DependencyLevel
                    > links.Single(l => l.RepositoryId == ctx.ProducerRepositoryId).DependencyLevel);
    }

    [Fact]
    public async Task Discarded_shell_package_references_are_not_attributed_to_the_retained_row()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        // The consumer repository also owns a package so the shell's reference has a valid target.
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ConsumerRepositoryId,
        [
            new SyncProjectInfo("Consumer", ProjectType.Library, "src/Consumer/Consumer.csproj", "net10.0", null, []),
            new SyncProjectInfo("Other.Lib", ProjectType.Package, "src/Other/Other.csproj", "net10.0", "Other.Lib", [])
        ]);

        IReadOnlyList<SyncProjectInfo> producer =
        [
            Shell(ShellPath, new SyncPackageReference("Other.Lib", "9.9.9")),
            Package(PackagePath, "Project")
        ];
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, producer);
        await repo.MergeWorkspaceProjectDependenciesAsync(ctx.WorkspaceId, [(ctx.ProducerRepositoryId, producer)]);

        Assert.Equal(0, await db.ProjectDependencies.CountAsync());
    }

    [Fact]
    public async Task Multiple_package_candidates_pick_a_deterministic_winner_by_path()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        var a = Package("src/B/B.csproj", "Project.B");
        var b = Package("src/a/A.csproj", "Project.A");
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, [a, b]);
        var first = await ctx.Db(scope).WorkspaceProjects.AsNoTracking().SingleAsync(p => p.RepositoryId == ctx.ProducerRepositoryId);
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, [b, a]);
        var second = await ctx.Db(scope).WorkspaceProjects.AsNoTracking().SingleAsync(p => p.RepositoryId == ctx.ProducerRepositoryId);

        Assert.Equal("src/a/A.csproj", first.ProjectFilePath);
        Assert.Equal(first.ProjectFilePath, second.ProjectFilePath);
        Assert.Equal("Project.A", second.PackageId);
    }

    [Fact]
    public void Selector_normalizes_separators_breaks_case_ties_ordinally_and_keeps_discovery_order_on_exact_tie()
    {
        var back = Package(@"src\Z\Z.csproj", "P");
        var forward = Package("src/z/z.csproj", "P");
        Assert.Equal("src/Z/Z.csproj", WorkspaceProjectRepository.SelectProjectsByName([forward, back])["Project"].ProjectFilePath.Replace('\\', '/'));

        var first = Package("x.csproj", "P1");
        var second = Package("x.csproj", "P2");
        Assert.Equal("P1", WorkspaceProjectRepository.SelectProjectsByName([first, second])["Project"].PackageId);
        Assert.Equal("P2", WorkspaceProjectRepository.SelectProjectsByName([second, first])["Project"].PackageId);
    }

    [Fact]
    public async Task Noncolliding_projects_are_unchanged()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId,
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
