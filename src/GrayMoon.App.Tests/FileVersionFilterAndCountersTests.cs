using GrayMoon.Common.FileVersions;
namespace GrayMoon.App.Tests;
public sealed class FileVersionFilterAndCountersTests
{
    [Fact]
    public void FilterPatternLinesToRepos_uses_repository_name_not_selector()
    {
        var pattern = """
            VERSION={@ServiceA}
            BRANCH={@ServiceA:branch}
            COMMIT={@ServiceA:commit}
            OTHER={@ServiceB:commit}
            """;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ServiceA" };
        var filtered = GrayMoon.App.Services.Workspaces.WorkspaceFileVersionService.FilterPatternLinesToRepos(pattern, allowed);
        Assert.Contains("VERSION={@ServiceA}", filtered);
        Assert.Contains("BRANCH={@ServiceA:branch}", filtered);
        Assert.Contains("COMMIT={@ServiceA:commit}", filtered);
        Assert.DoesNotContain("ServiceB", filtered);
    }
    [Fact]
    public void Three_token_kinds_for_same_repo_are_one_dependency_repository()
    {
        var tokens = FileVersionTokenParser.ExtractTokens("""
            V={@Repo}
            B={@Repo:branch}
            C={@Repo:commit}
            """);
        var repos = tokens.Select(t => t.RepositoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(new[] { "Repo" }, repos);
    }
    [Fact]
    public void Self_reference_kinds_share_repository_name()
    {
        var tokens = FileVersionTokenParser.ExtractTokens("""
            V={@CurrentRepo}
            B={@CurrentRepo:branch}
            C={@CurrentRepo:commit}
            """);
        Assert.All(tokens, t => Assert.Equal("CurrentRepo", t.RepositoryName));
    }
}
