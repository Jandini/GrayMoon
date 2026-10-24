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

    [Fact]
    public void A_repository_needs_the_packages_its_projects_restore_including_tests()
    {
        var format = new Node(10, 1, "format", "Edx1.Format");
        var formatExtra = new Node(11, 1, "format", "Edx1.Format.Extra");
        var formatTests = new Node(12, 1, "format", null);
        var io = new Node(20, 2, "io", "Edx1.IO");
        var ioAbstractions = new Node(21, 2, "io", "Edx1.IO.Abstractions");
        var compression = new Node(30, 3, "compression", "Edx1.Compression");
        var artifact = new Node(40, 4, "artifact", "Edx1.Artifact");

        var result = WorkspaceProjectRepository.ResolvePackageAccessNeeds(
            [format, formatExtra, formatTests, io, ioAbstractions, compression, artifact],
            [(10, 20), (10, 21), (10, 30), (12, 40)]);

        var needs = result[format.ProjectId];
        Assert.Equal(["Edx1.Artifact", "Edx1.Compression", "Edx1.IO", "Edx1.IO.Abstractions"], needs.Select(n => n.PackageId));
        Assert.All(needs, n => Assert.True(n.IsDirect));
        Assert.Same(needs, result[formatExtra.ProjectId]);
    }

    [Fact]
    public void Needs_follow_a_referenced_package_and_a_direct_reference_wins()
    {
        var format = new Node(10, 1, "format", "Edx1.Format");
        var email = new Node(20, 2, "email", "Edx1.File.Email");
        var backup = new Node(21, 5, "backup", "Edx1.File.Backup");
        var compression = new Node(30, 3, "compression", "Edx1.Compression");

        var result = WorkspaceProjectRepository.ResolvePackageAccessNeeds(
            [format, email, backup, compression],
            [(20, 10), (10, 30), (21, 10), (21, 30)]);

        var emailNeeds = result[email.ProjectId];
        Assert.Equal(["Edx1.Format", "Edx1.Compression"], emailNeeds.Select(n => n.PackageId));
        Assert.Equal("Edx1.Format", emailNeeds.Single(n => n.PackageId == "Edx1.Compression").ViaPackageId);
        Assert.True(result[backup.ProjectId].Single(n => n.PackageId == "Edx1.Compression").IsDirect);
        Assert.Equal(["Edx1.Compression"], result[format.ProjectId].Select(n => n.PackageId));
    }

    [Fact]
    public void Needs_omit_packages_published_by_the_same_repository_and_cycles_terminate()
    {
        var archive = new Node(10, 1, "archive", "Edx1.File.Archive");
        var abstractions = new Node(11, 1, "archive", "Edx1.File.Archive.Abstractions");
        var io = new Node(20, 2, "io", "Edx1.IO");

        var result = WorkspaceProjectRepository.ResolvePackageAccessNeeds(
            [archive, abstractions, io],
            [(10, 11), (11, 10), (10, 20)]);

        var needs = result[archive.ProjectId];
        var only = Assert.Single(needs);
        Assert.Equal("Edx1.IO", only.PackageId);
        Assert.True(only.IsDirect);
        Assert.Same(needs, result[abstractions.ProjectId]);
        Assert.False(result.ContainsKey(io.ProjectId));
    }

    [Fact]
    public void A_referenced_package_lists_the_other_packages_its_repository_publishes()
    {
        var mailbox = new Node(1, 1, "mailbox", "Edx1.File.Mailbox");
        var archive = new Node(2, 2, "archive", "Edx1.File.Archive");
        var abstractions = new Node(3, 2, "archive", "Edx1.File.Archive.Abstractions");
        var io = new Node(4, 3, "io", "Edx1.IO");

        var result = WorkspaceProjectRepository.ResolvePackageAccessNeeds(
            [mailbox, archive, abstractions, io],
            [(1, 2), (3, 4)]);

        var needs = result[mailbox.ProjectId];
        Assert.Equal(
            ["Edx1.File.Archive", "Edx1.File.Archive.Abstractions", "Edx1.IO"],
            needs.Select(n => n.PackageId));
        Assert.True(needs.Single(n => n.PackageId == "Edx1.File.Archive").IsDirect);
        Assert.Equal("Edx1.File.Archive", needs.Single(n => n.PackageId == "Edx1.File.Archive.Abstractions").ViaPackageId);
        Assert.Equal("Edx1.File.Archive.Abstractions", needs.Single(n => n.PackageId == "Edx1.IO").ViaPackageId);
    }
}
