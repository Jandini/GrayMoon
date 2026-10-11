using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

public class ProjectPackagingResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gm-pack-" + Guid.NewGuid().ToString("N"));
    private readonly CsProjFileParser _parser = new();

    public ProjectPackagingResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task IsPackable_false_with_PackageId_is_a_library_and_clears_the_package_id()
    {
        var path = WriteProject("src/Acme.Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>Acme.Lib</PackageId>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
            </Project>
            """);

        var info = await Parse(path);

        Assert.Equal(ProjectType.Library, info.ProjectType);
        Assert.Null(info.PackageId);
        Assert.Equal("Acme.Lib", info.Name);
    }

    [Fact]
    public async Task IsPackable_false_in_Directory_Build_props_overrides_PackageId()
    {
        Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
            </Project>
            """);
        var path = WriteProject("src/Acme.Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>Acme.Lib</PackageId>
              </PropertyGroup>
            </Project>
            """);

        var info = await Parse(path);

        Assert.Equal(ProjectType.Library, info.ProjectType);
        Assert.Null(info.PackageId);
    }

    [Fact]
    public async Task Project_IsPackable_true_overrides_props()
    {
        Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
            </Project>
            """);
        var path = WriteProject("src/Acme.Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>Acme.Lib</PackageId>
                <IsPackable>true</IsPackable>
              </PropertyGroup>
            </Project>
            """);

        var info = await Parse(path);

        Assert.Equal(ProjectType.Package, info.ProjectType);
        Assert.Equal("Acme.Lib", info.PackageId);
    }

    [Fact]
    public async Task Directory_Build_targets_overrides_the_project()
    {
        Write("Directory.Build.targets", """
            <Project>
              <PropertyGroup>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
            </Project>
            """);
        var path = WriteProject("src/Acme.Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsPackable>true</IsPackable>
              </PropertyGroup>
            </Project>
            """);

        var info = await Parse(path);

        Assert.Equal(ProjectType.Library, info.ProjectType);
        Assert.Null(info.PackageId);
    }

    [Fact]
    public async Task Nearest_Directory_Build_props_wins_over_a_higher_file_it_does_not_import()
    {
        Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
            </Project>
            """);
        Write("src/Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <IsPackable>true</IsPackable>
              </PropertyGroup>
            </Project>
            """);
        var path = WriteProject("src/Acme.Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var info = await Parse(path);

        Assert.Equal(ProjectType.Package, info.ProjectType);
        Assert.Equal("Acme.Lib", info.PackageId);
    }

    [Fact]
    public async Task GetPathOfFileAbove_parent_chain_applies_the_parent_then_the_child()
    {
        Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
            </Project>
            """);
        Write("src/Directory.Build.props", """
            <Project>
              <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
            </Project>
            """);
        var path = WriteProject("src/Acme.Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>Acme.Lib</PackageId>
              </PropertyGroup>
            </Project>
            """);

        var info = await Parse(path);

        Assert.Equal(ProjectType.Library, info.ProjectType);
        Assert.Null(info.PackageId);
    }

    [Fact]
    public async Task IsPackable_true_without_PackageId_is_still_a_package()
    {
        var path = WriteProject("src/NewLib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsPackable>true</IsPackable>
              </PropertyGroup>
            </Project>
            """);

        var info = await Parse(path);

        Assert.Equal(ProjectType.Package, info.ProjectType);
        Assert.Equal("NewLib", info.PackageId);
        Assert.Equal("NewLib", info.Name);
    }

    [Fact]
    public async Task ImportDirectoryBuildProps_false_ignores_Directory_Build_props()
    {
        Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
            </Project>
            """);
        var path = WriteProject("src/Acme.Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps>
                <PackageId>Acme.Lib</PackageId>
              </PropertyGroup>
            </Project>
            """);

        var info = await Parse(path);

        Assert.Equal(ProjectType.Package, info.ProjectType);
        Assert.Equal("Acme.Lib", info.PackageId);
    }

    [Fact]
    public async Task Literal_relative_import_is_applied_and_conditional_wildcard_and_outside_imports_are_ignored()
    {
        Write("src/Pack.props", """
            <Project>
              <PropertyGroup>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
            </Project>
            """);
        var applied = WriteProject("src/Applied.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="Pack.props" />
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>Applied.Lib</PackageId>
              </PropertyGroup>
            </Project>
            """);

        Write("src/Cond.props", """
            <Project>
              <PropertyGroup>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
            </Project>
            """);
        var conditional = WriteProject("src/Conditional.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="Cond.props" Condition="'true' == 'true'" />
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>Conditional.Lib</PackageId>
              </PropertyGroup>
            </Project>
            """);

        Write("src/Wild.props", """
            <Project>
              <PropertyGroup>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
            </Project>
            """);
        var wildcard = WriteProject("src/Wildcard.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="*.props" />
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>Wildcard.Lib</PackageId>
              </PropertyGroup>
            </Project>
            """);

        var outsideDir = Path.Combine(Path.GetTempPath(), "gm-pack-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsideProps = Path.Combine(outsideDir, "Outside.props");
        File.WriteAllText(outsideProps, """
            <Project>
              <PropertyGroup>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
            </Project>
            """);
        var relativeOutside = Path.GetRelativePath(Path.Combine(_root, "src"), outsideProps).Replace('\\', '/');
        var outside = WriteProject("src/Outside.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="{relativeOutside}" />
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>Outside.Lib</PackageId>
              </PropertyGroup>
            </Project>
            """);

        try
        {
            var appliedInfo = await Parse(applied);
            Assert.Equal(ProjectType.Library, appliedInfo.ProjectType);
            Assert.Null(appliedInfo.PackageId);

            var conditionalInfo = await Parse(conditional);
            Assert.Equal(ProjectType.Package, conditionalInfo.ProjectType);
            Assert.Equal("Conditional.Lib", conditionalInfo.PackageId);

            var wildcardInfo = await Parse(wildcard);
            Assert.Equal(ProjectType.Package, wildcardInfo.ProjectType);
            Assert.Equal("Wildcard.Lib", wildcardInfo.PackageId);

            var outsideInfo = await Parse(outside);
            Assert.Equal(ProjectType.Package, outsideInfo.ProjectType);
            Assert.Equal("Outside.Lib", outsideInfo.PackageId);
        }
        finally
        {
            if (Directory.Exists(outsideDir))
                Directory.Delete(outsideDir, recursive: true);
        }
    }

    private async Task<CsProjFileInfo> Parse(string path)
    {
        var info = await _parser.ParseAsync(path, repositoryRoot: _root);
        Assert.NotNull(info);
        return info;
    }

    private string WriteProject(string relativePath, string contents)
    {
        Write(relativePath, contents);
        return Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private void Write(string relativePath, string contents)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
