using GrayMoon.Common.Git;

namespace GrayMoon.Common.Tests;

public class MonacoLanguageMapperTests
{
    [Theory]
    [InlineData("Program.cs", "csharp")]
    [InlineData("Component.razor", "razor")]
    [InlineData("_Host.cshtml", "razor")]
    [InlineData("appsettings.json", "json")]
    [InlineData("web.config", "plaintext")]
    [InlineData("Project.csproj", "xml")]
    [InlineData("Directory.Build.props", "xml")]
    [InlineData("workflow.yml", "yaml")]
    [InlineData("workflow.yaml", "yaml")]
    [InlineData("script.js", "javascript")]
    [InlineData("module.mjs", "javascript")]
    [InlineData("component.tsx", "typescript")]
    [InlineData("types.ts", "typescript")]
    [InlineData("styles.css", "css")]
    [InlineData("styles.scss", "scss")]
    [InlineData("index.html", "html")]
    [InlineData("query.sql", "sql")]
    [InlineData("deploy.ps1", "powershell")]
    [InlineData("README.md", "markdown")]
    [InlineData("notes.txt", "plaintext")]
    [InlineData("Dockerfile", "plaintext")]
    public void Maps_extension_to_expected_language_id(string path, string expectedLanguageId)
    {
        Assert.Equal(expectedLanguageId, MonacoLanguageMapper.GetLanguageId(path));
    }

    [Fact]
    public void Nested_path_uses_only_the_file_extension()
    {
        Assert.Equal("csharp", MonacoLanguageMapper.GetLanguageId("src/Services/GitChanges/GitService.cs"));
    }

    [Fact]
    public void Extension_matching_is_case_insensitive()
    {
        Assert.Equal("csharp", MonacoLanguageMapper.GetLanguageId("Program.CS"));
    }
}
