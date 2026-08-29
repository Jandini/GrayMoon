using GrayMoon.App.Services;

namespace GrayMoon.App.Tests;

public sealed class GitHubApiUsageCategoryMapperTests
{
    [Theory]
    [InlineData("repos/acme/widgets/actions/runs/123/jobs?per_page=100", GitHubApiUsageCategoryMapper.Actions)]
    [InlineData("repos/acme/widgets/actions/runs?branch=main&per_page=20", GitHubApiUsageCategoryMapper.Actions)]
    [InlineData("repos/acme/widgets/actions/workflows/1/dispatches", GitHubApiUsageCategoryMapper.Actions)]
    [InlineData("repos/acme/widgets/pulls?state=open", GitHubApiUsageCategoryMapper.PullRequests)]
    [InlineData("repos/acme/widgets/pulls/42", GitHubApiUsageCategoryMapper.PullRequests)]
    [InlineData("repos/acme/widgets/collaborators?per_page=100", GitHubApiUsageCategoryMapper.Reviewers)]
    [InlineData("repos/acme/widgets/teams", GitHubApiUsageCategoryMapper.Reviewers)]
    [InlineData("rate_limit", GitHubApiUsageCategoryMapper.RateLimitCheck)]
    [InlineData("user", GitHubApiUsageCategoryMapper.Account)]
    [InlineData("user/repos?per_page=100", GitHubApiUsageCategoryMapper.Account)]
    [InlineData("repos/acme/widgets", GitHubApiUsageCategoryMapper.Repository)]
    [InlineData("repos/acme/widgets/branches/main", GitHubApiUsageCategoryMapper.Repository)]
    [InlineData("orgs/acme/repos", GitHubApiUsageCategoryMapper.Other)]
    [InlineData("", GitHubApiUsageCategoryMapper.Other)]
    [InlineData(null, GitHubApiUsageCategoryMapper.Other)]
    public void Categorize_MapsPathToExpectedCategory(string? requestUri, string expected)
    {
        Assert.Equal(expected, GitHubApiUsageCategoryMapper.Categorize(requestUri));
    }
}
