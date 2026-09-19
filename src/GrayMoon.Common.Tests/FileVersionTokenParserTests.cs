using GrayMoon.Common.FileVersions;
namespace GrayMoon.Common.Tests;
public sealed class FileVersionTokenParserTests
{
    [Theory]
    [InlineData("Repo", "Repo", FileVersionTokenKind.GitVersion, "@Repo")]
    [InlineData("@Repo", "Repo", FileVersionTokenKind.GitVersion, "@Repo")]
    [InlineData("@Repo:branch", "Repo", FileVersionTokenKind.Branch, "@Repo:branch")]
    [InlineData("@Repo:commit", "Repo", FileVersionTokenKind.Commit, "@Repo:commit")]
    [InlineData("Repo:BRANCH", "Repo", FileVersionTokenKind.Branch, "@Repo:branch")]
    [InlineData("@Repo:Commit", "Repo", FileVersionTokenKind.Commit, "@Repo:commit")]
    public void TryParse_supported_forms(string inner, string repo, FileVersionTokenKind kind, string key)
    {
        Assert.True(FileVersionTokenParser.TryParse(inner, out var token, out var unsupported));
        Assert.Null(unsupported);
        Assert.NotNull(token);
        Assert.Equal(repo, token!.RepositoryName);
        Assert.Equal(kind, token.Kind);
        Assert.Equal(key, token.TokenKey);
    }
    [Fact]
    public void TryParse_unsupported_selector_is_not_a_repository_name()
    {
        Assert.False(FileVersionTokenParser.TryParse("@Repo:foo", out var token, out var unsupported));
        Assert.Null(token);
        Assert.Equal("foo", unsupported);
    }
    [Fact]
    public void ExtractTokens_parses_all_three_kinds_and_skips_invalid()
    {
        var pattern = """
            VERSION={@Repo}
            BRANCH={@Repo:branch}
            COMMIT={@Repo:commit}
            BAD={@Repo:foo}
            """;
        var tokens = FileVersionTokenParser.ExtractTokens(pattern);
        Assert.Equal(3, tokens.Count);
        Assert.Equal(FileVersionTokenKind.GitVersion, tokens[0].Kind);
        Assert.Equal(FileVersionTokenKind.Branch, tokens[1].Kind);
        Assert.Equal(FileVersionTokenKind.Commit, tokens[2].Kind);
        Assert.All(tokens, t => Assert.Equal("Repo", t.RepositoryName));
    }
    [Fact]
    public void Validate_separates_unknown_repo_from_unsupported_selector()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ExistingRepo" };
        var pattern = """
            A={@MissingRepo:commit}
            B={@ExistingRepo:foo}
            C={@ExistingRepo}
            """;
        var result = FileVersionTokenParser.Validate(pattern, known);
        Assert.Single(result.ValidTokens);
        Assert.Equal("ExistingRepo", result.ValidTokens[0].RepositoryName);
        Assert.Contains("MissingRepo", result.UnknownRepositories);
        Assert.Contains("foo", result.UnsupportedSelectors);
        Assert.DoesNotContain(result.UnknownRepositories, r => r.Contains("foo", StringComparison.Ordinal));
        Assert.Empty(result.SelfReferencingTokens);
    }
    [Fact]
    public void Validate_detects_self_reference_to_owning_repository()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OwnRepo", "OtherRepo" };
        var pattern = """
            V={@OwnRepo}
            B={@OwnRepo:branch}
            C={@OwnRepo:commit}
            O={@OtherRepo}
            """;
        var result = FileVersionTokenParser.Validate(pattern, known, owningRepositoryName: "OwnRepo");
        Assert.Equal(4, result.ValidTokens.Count);
        Assert.Equal(3, result.SelfReferencingTokens.Count);
        Assert.All(result.SelfReferencingTokens, t => Assert.Equal("OwnRepo", t.RepositoryName));
        Assert.Contains(result.SelfReferencingTokens, t => t.Kind == FileVersionTokenKind.GitVersion);
        Assert.Contains(result.SelfReferencingTokens, t => t.Kind == FileVersionTokenKind.Branch);
        Assert.Contains(result.SelfReferencingTokens, t => t.Kind == FileVersionTokenKind.Commit);
    }
    [Fact]
    public void Validate_self_reference_is_case_insensitive()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MyRepo" };
        var result = FileVersionTokenParser.Validate("V={@myrepo:commit}", known, owningRepositoryName: "MyRepo");
        Assert.Single(result.SelfReferencingTokens);
        Assert.Equal(FileVersionTokenKind.Commit, result.SelfReferencingTokens[0].Kind);
    }
    [Fact]
    public void ParsePatternLines_preserves_prefix_and_suffix()
    {
        var entries = FileVersionTokenParser.ParsePatternLines("Version=\"{@Repo}\" />");
        Assert.Single(entries);
        Assert.Equal("Version=\"", entries[0].Prefix);
        Assert.Equal("Repo", entries[0].Token.RepositoryName);
        Assert.Equal(FileVersionTokenKind.GitVersion, entries[0].Token.Kind);
        Assert.Equal("\" />", entries[0].Suffix);
    }
    [Fact]
    public void ParsePatternLines_commit_token_key()
    {
        var entries = FileVersionTokenParser.ParsePatternLines("COMMIT={@Repo:commit}");
        Assert.Single(entries);
        Assert.Equal("COMMIT=", entries[0].Prefix);
        Assert.Equal("@Repo:commit", entries[0].Token.TokenKey);
        Assert.Equal("", entries[0].Suffix);
    }
    [Fact]
    public void ExtractTokens_same_repo_three_kinds_are_distinct_keys_same_repository()
    {
        var tokens = FileVersionTokenParser.ExtractTokens("""
            V={@Repo}
            B={@Repo:branch}
            C={@Repo:commit}
            """);
        Assert.Equal(3, tokens.Select(t => t.TokenKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Single(tokens.Select(t => t.RepositoryName).Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
