using GrayMoon.Common.Git;

namespace GrayMoon.Common.Tests;

public sealed class WorkspaceDefinitionTagPinTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Absent_definition_has_no_pin()
    {
        var (commit, error) = WorkspaceDefinitionTagPin.ReadCommit(null, "https://github.com/acme/Api.git");

        Assert.Null(commit);
        Assert.Null(error);
    }

    [Fact]
    public void Repository_without_a_pin_is_not_checked_out()
    {
        const string json = """
            { "repositories": [ { "name": "Api", "repositoryUrl": "https://github.com/acme/Api.git" } ] }
            """;

        var (commit, error) = WorkspaceDefinitionTagPin.ReadCommit(json, "https://github.com/acme/Api.git");

        Assert.Null(commit);
        Assert.Null(error);
    }

    [Fact]
    public void Pin_matches_the_repository_url_and_returns_the_full_commit()
    {
        var json = $$"""
            { "repositories": [ { "name": "Api", "repositoryUrl": "https://github.com/acme/Api.git", "tag": "v1.0.0", "commit": "{{Sha.ToUpperInvariant()}}" } ] }
            """;

        var (commit, error) = WorkspaceDefinitionTagPin.ReadCommit(json, "https://github.com/acme/Api");

        Assert.Null(error);
        Assert.Equal(Sha, commit);
    }

    [Fact]
    public void Invalid_json_is_an_error()
    {
        var (commit, error) = WorkspaceDefinitionTagPin.ReadCommit("{", "https://github.com/acme/Api.git");

        Assert.Null(commit);
        Assert.Contains("not valid JSON", error);
    }

    [Fact]
    public void Tag_without_commit_is_an_error()
    {
        const string json = """
            { "repositories": [ { "name": "Api", "repositoryUrl": "https://github.com/acme/Api.git", "tag": "v1.0.0" } ] }
            """;

        var (commit, error) = WorkspaceDefinitionTagPin.ReadCommit(json, "https://github.com/acme/Api.git");

        Assert.Null(commit);
        Assert.Equal("Repository 'Api' is on a tag but is missing the tag name or the full commit hash.", error);
    }

    [Fact]
    public void Short_commit_is_an_error()
    {
        const string json = """
            { "repositories": [ { "name": "Api", "repositoryUrl": "https://github.com/acme/Api.git", "tag": "v1.0.0", "commit": "abc" } ] }
            """;

        var (commit, error) = WorkspaceDefinitionTagPin.ReadCommit(json, "https://github.com/acme/Api.git");

        Assert.Null(commit);
        Assert.Equal("Repository 'Api' commit must be the full commit hash.", error);
    }
}
