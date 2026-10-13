using GrayMoon.Common.Git;

namespace GrayMoon.Common.Tests;

public sealed class WorkspaceDefinitionRepositoriesTests
{
    [Fact]
    public void Reads_every_listed_repository_name()
    {
        var names = WorkspaceDefinitionRepositories.ReadNames("""
            {
              "version": 1,
              "repositories": [
                { "name": "GrayMoon", "repositoryUrl": "https://github.com/acme/GrayMoon.git" },
                { "Name": " GrayMoon.Desktop " }
              ]
            }
            """);

        Assert.Equal(["GrayMoon", "GrayMoon.Desktop"], names);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "version": 1 }""")]
    [InlineData("""{ "repositories": {} }""")]
    public void Missing_or_unreadable_definition_lists_nothing(string? json)
        => Assert.Empty(WorkspaceDefinitionRepositories.ReadNames(json));

    [Fact]
    public void Skips_entries_that_cannot_be_a_single_folder_name()
    {
        var names = WorkspaceDefinitionRepositories.ReadNames("""
            { "repositories": [ "Api", { "name": 5 }, { "name": "" }, { "name": ".." }, { "name": "a/b" }, { "name": "a\\b" }, { "name": "Api" } ] }
            """);

        Assert.Equal(["Api"], names);
    }

    [Fact]
    public void Reads_the_definition_at_the_repository_root()
    {
        var folder = Directory.CreateTempSubdirectory("graymoon-definition-").FullName;
        try
        {
            Assert.Empty(WorkspaceDefinitionRepositories.ReadNamesFromDirectory(folder));

            File.WriteAllText(Path.Combine(folder, WorkspaceDefinitionRepositories.FileName), """{ "repositories": [ { "name": "Api" } ] }""");

            Assert.Equal(["Api"], WorkspaceDefinitionRepositories.ReadNamesFromDirectory(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
