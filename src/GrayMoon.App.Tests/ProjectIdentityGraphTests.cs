using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>Graph edge maintenance resolves dependents by physical path and producers by unambiguous PackageId.</summary>
public sealed class ProjectIdentityGraphTests
{
    private static SyncProjectInfo App(string path, string package, string version) =>
        new("App", ProjectType.Library, path, "net10.0", null, [new SyncPackageReference(package, version)]);

    [Fact]
    public async Task Version_update_changes_only_the_edge_of_the_consumer_at_that_path()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        IReadOnlyList<SyncProjectInfo> producer = [new("Lib", ProjectType.Package, "lib/Lib.csproj", "net10.0", "Lib.Pkg", [])];
        IReadOnlyList<SyncProjectInfo> consumers = [App("one/App.csproj", "Lib.Pkg", "1.0.0"), App("two/App.csproj", "Lib.Pkg", "1.0.0")];
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, producer);
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ConsumerRepositoryId, consumers);
        await repo.MergeWorkspaceProjectDependenciesAsync(ctx.WorkspaceId, [(ctx.ConsumerRepositoryId, (IReadOnlyList<SyncProjectInfo>?)consumers)]);

        await repo.UpdateProjectDependencyVersionsAsync(ctx.WorkspaceId, [(ctx.ConsumerRepositoryId, @"ONE\App.csproj", "Lib.Pkg", "2.0.0")]);

        var one = await db.WorkspaceProjects.SingleAsync(p => p.ProjectFilePath == "one/App.csproj");
        var two = await db.WorkspaceProjects.SingleAsync(p => p.ProjectFilePath == "two/App.csproj");
        Assert.Equal("2.0.0", (await db.ProjectDependencies.AsNoTracking().SingleAsync(d => d.DependentProjectId == one.ProjectId)).Version);
        Assert.Equal("1.0.0", (await db.ProjectDependencies.AsNoTracking().SingleAsync(d => d.DependentProjectId == two.ProjectId)).Version);
    }

    [Fact]
    public async Task Version_update_for_an_ambiguous_package_changes_nothing()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        IReadOnlyList<SyncProjectInfo> producer = [new("Lib", ProjectType.Package, "lib/Lib.csproj", "net10.0", "Lib.Pkg", [])];
        IReadOnlyList<SyncProjectInfo> consumers = [App("one/App.csproj", "Lib.Pkg", "1.0.0")];
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, producer);
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ConsumerRepositoryId, consumers);
        await repo.MergeWorkspaceProjectDependenciesAsync(ctx.WorkspaceId, [(ctx.ConsumerRepositoryId, (IReadOnlyList<SyncProjectInfo>?)consumers)]);
        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId,
            [.. producer, new("Lib2", ProjectType.Package, "lib2/Lib2.csproj", "net10.0", "Lib.Pkg", [])]);

        await repo.UpdateProjectDependencyVersionsAsync(ctx.WorkspaceId, [(ctx.ConsumerRepositoryId, "one/App.csproj", "Lib.Pkg", "9.9.9")]);

        Assert.DoesNotContain(await db.ProjectDependencies.AsNoTracking().ToListAsync(), d => d.Version == "9.9.9");
    }

    [Fact]
    public async Task Generated_edges_find_the_consumer_whatever_the_path_formatting()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);

        await ctx.Repo(scope).SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId,
            [ctx.MakeInfo(@"SRC\Consumer\Consumer.csproj", "1.0.0")]);

        var edge = await db.ProjectDependencies.AsNoTracking().SingleAsync();
        Assert.Equal(ctx.ConsumerProjectId, edge.DependentProjectId);
    }
}
