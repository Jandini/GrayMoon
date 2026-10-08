using GrayMoon.App.Components.Modals;
using Xunit;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceRepositoryPickerFilterTests
{
    private static readonly IReadOnlyList<WorkspaceModal.WorkspaceRepositoryChoice> Choices =
    [
        new(1, "acme/alpha"),
        new(2, "acme/Beta-Service"),
        new(3, "other/gamma"),
    ];

    [Fact]
    public void Filter_ContainsMatch_ReturnsMatching()
    {
        var result = WorkspaceModal.FilterWorkspaceRepositoryChoices(Choices, "gamma");
        Assert.Equal([3], result.Select(c => c.RepositoryId));
    }

    [Fact]
    public void Filter_IsCaseInsensitive()
    {
        var result = WorkspaceModal.FilterWorkspaceRepositoryChoices(Choices, "BETA-serv");
        Assert.Equal([2], result.Select(c => c.RepositoryId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Filter_EmptyTerm_ReturnsAll(string? term)
    {
        var result = WorkspaceModal.FilterWorkspaceRepositoryChoices(Choices, term);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Filter_PreservesOrder()
    {
        var result = WorkspaceModal.FilterWorkspaceRepositoryChoices(Choices, "a");
        Assert.Equal([1, 2, 3], result.Select(c => c.RepositoryId));
    }

    [Theory]
    [InlineData(0, 1, 3, 1)]
    [InlineData(3, 1, 3, 3)]
    [InlineData(0, -1, 3, 0)]
    [InlineData(2, -1, 3, 1)]
    [InlineData(0, 1, 0, 0)]
    [InlineData(1, 1, 0, 0)]
    public void MoveHighlight_ClampsAtEnds(int current, int delta, int filteredCount, int expected)
    {
        Assert.Equal(expected, WorkspaceModal.MoveRepositoryHighlight(current, delta, filteredCount));
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    public void ResetHighlight_PicksFirstFilteredEntryOrNone(int filteredCount, int expected)
    {
        Assert.Equal(expected, WorkspaceModal.ResetRepositoryHighlight(filteredCount));
    }
}