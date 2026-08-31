using GrayMoon.Agent.Services;

namespace GrayMoon.Agent.Tests;

public class CsProjFileParserTests
{
    private static string WriteTempCsproj(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gm-parser-test-{Guid.NewGuid():N}.csproj");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public async Task FindPackageReferenceForVersionPatternAsync_matches_self_closing_element_on_single_line()
    {
        var path = WriteTempCsproj(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="GrayMoon.Abstractions" Version="1.2.3" />
              </ItemGroup>
            </Project>
            """);
        try
        {
            var parser = new CsProjFileParser();
            var result = await parser.FindPackageReferenceForVersionPatternAsync(path, "Version=\"", "\" />");

            Assert.NotNull(result);
            Assert.Equal("GrayMoon.Abstractions", result.Include);
            Assert.Equal("1.2.3", result.Version);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FindPackageReferenceForVersionPatternAsync_matches_multiline_attributes_regardless_of_order()
    {
        var path = WriteTempCsproj(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference
                    Version="9.9.9"
                    Include="GrayMoon.Common" />
              </ItemGroup>
            </Project>
            """);
        try
        {
            var parser = new CsProjFileParser();
            var result = await parser.FindPackageReferenceForVersionPatternAsync(path, "Version=\"", "\"");

            Assert.NotNull(result);
            Assert.Equal("GrayMoon.Common", result.Include);
            Assert.Equal("9.9.9", result.Version);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FindPackageReferenceForVersionPatternAsync_matches_child_element_form()
    {
        var path = WriteTempCsproj(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="GrayMoon.Common">
                  <Version>4.5.6</Version>
                </PackageReference>
              </ItemGroup>
            </Project>
            """);
        try
        {
            var parser = new CsProjFileParser();
            var result = await parser.FindPackageReferenceForVersionPatternAsync(path, "<Version>", "</Version>");

            Assert.NotNull(result);
            Assert.Equal("GrayMoon.Common", result.Include);
            Assert.Equal("4.5.6", result.Version);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FindPackageReferenceForVersionPatternAsync_ignores_matches_outside_PackageReference_elements()
    {
        var path = WriteTempCsproj(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <SomeUnrelatedTag Version="1.0.0" />
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="GrayMoon.Common" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);
        try
        {
            var parser = new CsProjFileParser();
            var result = await parser.FindPackageReferenceForVersionPatternAsync(path, "Version=\"1.0.0\" ", "");

            // The pattern only matches the unrelated tag's Version attribute, which is not inside a
            // PackageReference element, so no package should be resolved.
            Assert.Null(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FindPackageReferenceForVersionPatternAsync_returns_null_when_no_element_matches()
    {
        var path = WriteTempCsproj(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="GrayMoon.Common" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);
        try
        {
            var parser = new CsProjFileParser();
            var result = await parser.FindPackageReferenceForVersionPatternAsync(path, "Version=\"", "\" NoSuchSuffix");

            Assert.Null(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FindPackageReferenceForVersionPatternAsync_returns_null_when_file_missing()
    {
        var parser = new CsProjFileParser();
        var result = await parser.FindPackageReferenceForVersionPatternAsync(
            Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.csproj"), "Version=\"", "\"");

        Assert.Null(result);
    }
}
