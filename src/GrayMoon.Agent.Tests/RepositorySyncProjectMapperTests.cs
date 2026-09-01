using GrayMoon.Abstractions.Notifications;
using GrayMoon.Agent.Commands;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Tests;

public sealed class RepositorySyncProjectMapperTests
{
    [Fact]
    public void ToNotifications_maps_csproj_package_references_onto_the_sync_payload()
    {
        IReadOnlyList<CsProjFileInfo> projects =
        [
            new CsProjFileInfo
            {
                Name = "Acme.Api",
                ProjectType = ProjectType.Service,
                ProjectPath = "src/Acme.Api/Acme.Api.csproj",
                TargetFramework = "net10.0",
                PackageId = null,
                PackageReferences =
                [
                    new NuGetPackageReference { Name = "Acme.Lib", Version = "2.0.0" }
                ]
            }
        ];

        var notifications = RepositorySyncProjectMapper.ToNotifications(projects);

        var project = Assert.Single(notifications!);
        Assert.Equal("Acme.Api", project.Name);
        Assert.Equal((int)ProjectType.Service, project.ProjectType);
        Assert.Equal("src/Acme.Api/Acme.Api.csproj", project.ProjectPath);
        var package = Assert.Single(project.PackageReferences!);
        Assert.Equal("Acme.Lib", package.Name);
        Assert.Equal("2.0.0", package.Version);
    }

    [Fact]
    public void ToNotifications_returns_null_when_the_scan_did_not_run()
    {
        Assert.Null(RepositorySyncProjectMapper.ToNotifications(null));
    }
}
