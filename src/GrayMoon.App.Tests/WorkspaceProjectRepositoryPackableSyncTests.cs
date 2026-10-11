using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

/// <summary>
/// A project that stops being packable is updated in place on sync, and package references no longer target it.
/// </summary>
public sealed class WorkspaceProjectRepositoryPackableSyncTests
{
    [Fact]
    public async Task Merge_reclassifies_an_existing_package_in_place_and_inserts_a_new_one()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId,
        [
            new SyncProjectInfo("Acme.Lib", ProjectType.Package, "src/Acme.Lib/Acme.Lib.csproj", "net10.0", "Acme.Lib", [])
        ]);

        var before = await ctx.Db(scope).WorkspaceProjects.SingleAsync(p => p.ProjectName == "Acme.Lib");

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId,
        [
            new SyncProjectInfo("Acme.Lib", ProjectType.Library, "src/Acme.Lib/Acme.Lib.csproj", "net10.0", null, []),
            new SyncProjectInfo("Acme.New", ProjectType.Package, "src/Acme.New/Acme.New.csproj", "net10.0", "Acme.New", [])
        ]);

        var db = ctx.Db(scope);
        var kept = await db.WorkspaceProjects.SingleAsync(p => p.ProjectName == "Acme.Lib");
        Assert.Equal(before.ProjectId, kept.ProjectId);
        Assert.Equal("Acme.Lib", kept.ProjectName);
        Assert.Equal(ProjectType.Library, kept.ProjectType);
        Assert.Null(kept.PackageId);

        var added = await db.WorkspaceProjects.SingleAsync(p => p.ProjectName == "Acme.New");
        Assert.Equal(ProjectType.Package, added.ProjectType);
        Assert.Equal("Acme.New", added.PackageId);
    }

    [Fact]
    public async Task Dependency_edge_to_a_project_that_lost_its_package_id_is_removed()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId,
        [
            new SyncProjectInfo("Acme.Lib", ProjectType.Package, "src/Acme.Lib/Acme.Lib.csproj", "net10.0", "Acme.Lib", [])
        ]);

        var consumer = await db.WorkspaceProjects.SingleAsync(p => p.ProjectId == ctx.ConsumerProjectId);
        await repo.MergeWorkspaceProjectDependenciesAsync(
            ctx.WorkspaceId,
            [
                (ctx.ConsumerRepositoryId, (IReadOnlyList<SyncProjectInfo>?)[
                    new SyncProjectInfo(consumer.ProjectName, ProjectType.Library, consumer.ProjectFilePath, "net10.0", null,
                        [new SyncPackageReference("Acme.Lib", "1.0.0")])
                ]),
                (ctx.ProducerRepositoryId, (IReadOnlyList<SyncProjectInfo>?)[
                    new SyncProjectInfo("Acme.Lib", ProjectType.Package, "src/Acme.Lib/Acme.Lib.csproj", "net10.0", "Acme.Lib", [])
                ])
            ],
            persistDependencyLevel: false);

        Assert.Equal(1, await db.ProjectDependencies.CountAsync());

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId,
        [
            new SyncProjectInfo("Acme.Lib", ProjectType.Library, "src/Acme.Lib/Acme.Lib.csproj", "net10.0", null, [])
        ]);

        await repo.MergeWorkspaceProjectDependenciesAsync(
            ctx.WorkspaceId,
            [
                (ctx.ConsumerRepositoryId, (IReadOnlyList<SyncProjectInfo>?)[
                    new SyncProjectInfo(consumer.ProjectName, ProjectType.Library, consumer.ProjectFilePath, "net10.0", null,
                        [new SyncPackageReference("Acme.Lib", "1.0.0")])
                ]),
                (ctx.ProducerRepositoryId, (IReadOnlyList<SyncProjectInfo>?)[
                    new SyncProjectInfo("Acme.Lib", ProjectType.Library, "src/Acme.Lib/Acme.Lib.csproj", "net10.0", null, [])
                ])
            ],
            persistDependencyLevel: false);

        Assert.Equal(0, await db.ProjectDependencies.CountAsync());
        var kept = await db.WorkspaceProjects.SingleAsync(p => p.ProjectName == "Acme.Lib");
        Assert.Equal(ProjectType.Library, kept.ProjectType);
        Assert.Null(kept.PackageId);
    }

    [Fact]
    public async Task Merge_does_not_delete_a_generated_package_when_a_real_project_stops_being_packable()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        await repo.SyncGeneratedPackageDependenciesAsync(ctx.WorkspaceId, [ctx.MakeInfo()]);

        await repo.MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId,
        [
            new SyncProjectInfo("Acme.Lib", ProjectType.Library, "src/Acme.Lib/Acme.Lib.csproj", "net10.0", null, [])
        ]);

        var db = ctx.Db(scope);
        Assert.Equal(1, await db.WorkspaceProjects.CountAsync(p => p.IsGenerated && p.PackageId == GeneratedPackageTestContext.PackageName));
    }
}
