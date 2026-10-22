using GrayMoon.Common.Manifest;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

public class CentralPackageVersionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gm-cpm-{Guid.NewGuid():N}");

    public CentralPackageVersionsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "App"));
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"),
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Edx1.IO" Version="0.1.0-next.3" />
                <PackageVersion Include="Other.Lib" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(CsprojPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Edx1.IO" />
                <PackageReference Include="Own.Version" Version="9.9.9" />
              </ItemGroup>
            </Project>
            """);
    }

    private string CsprojPath => Path.Combine(_root, "src", "App", "App.csproj");

    private string PropsPath => Path.Combine(_root, "Directory.Packages.props");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Parse_fills_empty_reference_version_from_directory_packages_props()
    {
        var info = await new CsProjFileParser().ParseAsync(CsprojPath, repositoryRoot: _root);

        Assert.NotNull(info);
        Assert.Equal("0.1.0-next.3", info.PackageReferences.Single(r => r.Name == "Edx1.IO").Version);
        Assert.Equal("9.9.9", info.PackageReferences.Single(r => r.Name == "Own.Version").Version);
    }

    [Fact]
    public void FindPropsFile_does_not_leave_the_repository_root()
    {
        var inner = Path.Combine(_root, "inner");
        Directory.CreateDirectory(inner);
        File.Delete(PropsPath);

        Assert.Null(CentralPackageVersions.FindPropsFile(Path.Combine(inner, "X.csproj"), inner));
    }

    [Fact]
    public async Task UpdatePackageVersions_rewrites_the_central_version_and_counts_the_props_file()
    {
        var service = NewService();

        var updated = await service.UpdatePackageVersionsAsync(_root,
            [(Path.Combine("src", "App", "App.csproj"), new Dictionary<string, string> { ["Edx1.IO"] = "0.1.0-next.7" })]);

        Assert.Equal(1, updated);
        var text = File.ReadAllText(PropsPath);
        Assert.Contains("Include=\"Edx1.IO\" Version=\"0.1.0-next.7\"", text);
        Assert.Contains("Include=\"Other.Lib\" Version=\"2.0.0\"", text);
    }

    [Fact]
    public async Task UpdatePackageVersions_reports_no_change_when_central_version_already_matches()
    {
        var service = NewService();

        var updated = await service.UpdatePackageVersionsAsync(_root,
            [(Path.Combine("src", "App", "App.csproj"), new Dictionary<string, string> { ["Edx1.IO"] = "0.1.0-next.3" })]);

        Assert.Equal(0, updated);
    }

    [Fact]
    public async Task UpdatePackageVersions_still_rewrites_a_version_that_lives_in_the_project()
    {
        var service = NewService();

        var updated = await service.UpdatePackageVersionsAsync(_root,
            [(Path.Combine("src", "App", "App.csproj"), new Dictionary<string, string> { ["Own.Version"] = "10.0.0" })]);

        Assert.Equal(1, updated);
        Assert.Contains("Version=\"10.0.0\"", File.ReadAllText(CsprojPath));
        Assert.Contains("Include=\"Edx1.IO\" Version=\"0.1.0-next.3\"", File.ReadAllText(PropsPath));
    }

    private static CsProjFileService NewService() => new(new CsProjFileParser(), new LibGit2SharpGitIgnoreService(), new WorkspaceManifestFile(), NullLogger<CsProjFileService>.Instance);
}
