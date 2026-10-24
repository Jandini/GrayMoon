using GrayMoon.App.Repositories;
using Node = GrayMoon.App.Repositories.WorkspaceProjectRepository.PackageDependentsNode;

namespace GrayMoon.App.Tests;

public class PackageDependentRepositoriesTests
{
    // Repositories: 1 core, 2 data, 3 api, 4 tools
    private static readonly Node Core = new(10, 1, "core", "Contoso.Core");
    private static readonly Node CoreTests = new(11, 1, "core", null);
    private static readonly Node Data = new(20, 2, "data", "Contoso.Data");
    private static readonly Node Api = new(30, 3, "api", null);
    private static readonly Node Tools = new(40, 4, "tools", null);

    [Fact]
    public void Direct_and_transitive_consumers_are_listed_without_the_owning_repository()
    {
        var result = WorkspaceProjectRepository.ResolvePackageDependentRepositories(
            [Core, CoreTests, Data, Api, Tools],
            [(11, 10), (20, 10), (30, 20)]);

        var core = result[Core.ProjectId];
        Assert.Equal(["data", "api"], core.Select(r => r.RepositoryName));
        Assert.True(core[0].IsDirect);
        Assert.Equal("Contoso.Data", core[1].ViaPackageId);

        var data = Assert.Single(result[Data.ProjectId]);
        Assert.Equal("api", data.RepositoryName);
        Assert.True(data.IsDirect);
    }

    [Fact]
    public void A_repository_reached_directly_and_transitively_is_direct()
    {
        var result = WorkspaceProjectRepository.ResolvePackageDependentRepositories(
            [Core, Data, Api],
            [(20, 10), (30, 20), (30, 10)]);

        var api = result[Core.ProjectId].Single(r => r.RepositoryName == "api");
        Assert.True(api.IsDirect);
    }

    [Fact]
    public void Unused_package_has_no_entry_and_cycles_terminate()
    {
        var result = WorkspaceProjectRepository.ResolvePackageDependentRepositories(
            [Core, Data, Tools],
            [(20, 10), (10, 20)]);

        Assert.Equal(["data"], result[Core.ProjectId].Select(r => r.RepositoryName));
        Assert.Equal(["core"], result[Data.ProjectId].Select(r => r.RepositoryName));
        Assert.False(result.ContainsKey(Tools.ProjectId));
    }
}
