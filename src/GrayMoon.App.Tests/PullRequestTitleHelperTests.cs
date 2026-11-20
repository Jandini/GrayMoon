using GrayMoon.App.Services.GitHub;

namespace GrayMoon.App.Tests;

public sealed class PullRequestTitleHelperTests
{
    [Theory]
    [InlineData("feature/ABC-123-add-login", "ABC-123 feature add login")]
    [InlineData("ABC-123_add_login", "ABC-123 add login")]
    [InlineData("add-login", "add login")]
    [InlineData("", "")]
    public void Default_title_leads_with_the_ticket(string branch, string expected) =>
        Assert.Equal(expected, PullRequestTitleHelper.BuildDefaultTitle(branch));

    [Theory]
    [InlineData("feature/ABC-123-add-login", "ABC-123")]
    [InlineData("ABC-123", "ABC-123")]
    [InlineData("add-login", null)]
    [InlineData("abc-123-add-login", null)]
    [InlineData(null, null)]
    public void Ticket_is_found_the_way_the_default_title_finds_it(string? branch, string? expected) =>
        Assert.Equal(expected, PullRequestTitleHelper.FindTicketId(branch));

    [Fact]
    public void Ai_title_gets_the_branch_ticket_in_front() =>
        Assert.Equal(
            "ABC-123 Add login page",
            PullRequestTitleHelper.PrefixWithTicket("Add login page", "feature/ABC-123-add-login"));

    [Theory]
    [InlineData("ABC-123 Add login page")]
    [InlineData("ABC-123: Add login page")]
    [InlineData("[abc-123] Add login page")]
    [InlineData("Add login page (ABC-123)")]
    public void Ai_title_that_already_names_the_ticket_is_not_prefixed_again(string title) =>
        Assert.Equal(title, PullRequestTitleHelper.PrefixWithTicket(title, "feature/ABC-123-add-login"));

    [Fact]
    public void Another_ticket_that_only_starts_the_same_does_not_count_as_named() =>
        Assert.Equal(
            "ABC-12 Follow up to ABC-123",
            PullRequestTitleHelper.PrefixWithTicket("Follow up to ABC-123", "ABC-12-follow-up"));

    [Theory]
    [InlineData("Add login page", "add-login", "Add login page")]
    [InlineData("Add login page", null, "Add login page")]
    [InlineData("", "feature/ABC-123-add-login", "")]
    [InlineData(null, "feature/ABC-123-add-login", "")]
    public void Nothing_is_added_without_a_ticket_or_a_title(string? title, string? branch, string expected) =>
        Assert.Equal(expected, PullRequestTitleHelper.PrefixWithTicket(title, branch));
}
