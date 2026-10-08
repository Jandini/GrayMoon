using GrayMoon.App.Components.Modals;
using GrayMoon.App.Components.Shared;
using Xunit;

namespace GrayMoon.App.Tests;

/// <summary>
/// The shared searchable repository picker used by Add/Edit Workspace (with "None") and Restore Workspace (without).
/// The Add Workspace cases pin the behaviour the picker had before it was extracted from WorkspaceModal.
/// </summary>
public sealed class RepositoryPickerStateTests
{
    private static readonly IReadOnlyList<RepositoryPickerChoice> Choices =
    [
        new(1, "acme/alpha"),
        new(2, "acme/Beta-Service"),
        new(3, "other/gamma"),
    ];

    private static RepositoryPickerState OpenPicker(bool allowNone, int? selected = null)
    {
        var state = new RepositoryPickerState { AllowNone = allowNone };
        state.Open(Choices, selected);
        return state;
    }

    [Fact]
    public void Filter_ContainsMatch_ReturnsMatching()
    {
        var result = RepositoryPickerState.FilterChoices(Choices, "gamma");
        Assert.Equal([3], result.Select(c => c.RepositoryId));
    }

    [Fact]
    public void Filter_IsCaseInsensitive()
    {
        var result = RepositoryPickerState.FilterChoices(Choices, "BETA-serv");
        Assert.Equal([2], result.Select(c => c.RepositoryId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Filter_EmptyTerm_ReturnsAll(string? term)
    {
        var result = RepositoryPickerState.FilterChoices(Choices, term);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Filter_PreservesOrder()
    {
        var result = RepositoryPickerState.FilterChoices(Choices, "a");
        Assert.Equal([1, 2, 3], result.Select(c => c.RepositoryId));
    }

    [Theory]
    [InlineData(0, 1, 4, 1)]
    [InlineData(3, 1, 4, 3)]
    [InlineData(0, -1, 4, 0)]
    [InlineData(2, -1, 4, 1)]
    [InlineData(0, 1, 1, 0)]
    [InlineData(1, 1, 1, 0)]
    [InlineData(0, 1, 0, 0)]
    public void MoveHighlight_ClampsAtEnds(int current, int delta, int rowCount, int expected)
    {
        Assert.Equal(expected, RepositoryPickerState.MoveHighlight(current, delta, rowCount));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Opens_with_all_choices_and_an_empty_filter(bool allowNone)
    {
        var state = OpenPicker(allowNone);

        Assert.True(state.IsOpen);
        Assert.Equal(string.Empty, state.Filter);
        Assert.Equal(3, state.Filtered(Choices).Count);
    }

    [Fact]
    public void Opening_highlights_the_current_selection()
    {
        Assert.True(OpenPicker(allowNone: true, selected: 2).IsChoiceHighlighted(1));
        Assert.True(OpenPicker(allowNone: false, selected: 2).IsChoiceHighlighted(1));

        // Nothing selected: "None" when offered, otherwise the first repository.
        Assert.True(OpenPicker(allowNone: true).IsNoneHighlighted);
        var restore = OpenPicker(allowNone: false);
        Assert.False(restore.IsNoneHighlighted);
        Assert.True(restore.IsChoiceHighlighted(0));
    }

    [Fact]
    public void Reopening_resets_the_filter()
    {
        var state = OpenPicker(allowNone: false);
        state.SetFilter(Choices, "gamma");
        state.Close();

        state.Open(Choices, null);

        Assert.Equal(string.Empty, state.Filter);
        Assert.Equal(3, state.Filtered(Choices).Count);
    }

    [Fact]
    public void Closed_input_shows_the_selected_display_name()
    {
        Assert.Equal("acme/Beta-Service", RepositoryPickerState.ClosedText(Choices, 2, allowNone: true, "None"));
        Assert.Equal("acme/Beta-Service", RepositoryPickerState.ClosedText(Choices, 2, allowNone: false, "None"));

        // Add Workspace shows "None"; Restore leaves the input empty so its placeholder ("Select a repository") shows.
        Assert.Equal("None", RepositoryPickerState.ClosedText(Choices, null, allowNone: true, "None"));
        Assert.Equal(string.Empty, RepositoryPickerState.ClosedText(Choices, null, allowNone: false, "None"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Typing_highlights_the_first_match(bool allowNone)
    {
        var state = OpenPicker(allowNone, selected: 3);

        state.SetFilter(Choices, "ACME");

        Assert.Equal([1, 2], state.Filtered(Choices).Select(c => c.RepositoryId));
        Assert.True(state.IsChoiceHighlighted(0));
    }

    [Fact]
    public void Arrow_keys_move_the_highlight_and_stop_at_the_ends()
    {
        var state = OpenPicker(allowNone: false);

        Assert.Null(state.HandleKey("ArrowDown", Choices));
        Assert.True(state.IsChoiceHighlighted(1));
        state.HandleKey("ArrowDown", Choices);
        state.HandleKey("ArrowDown", Choices);
        Assert.True(state.IsChoiceHighlighted(2));

        Assert.Null(state.HandleKey("ArrowUp", Choices));
        Assert.True(state.IsChoiceHighlighted(1));
        state.HandleKey("ArrowUp", Choices);
        state.HandleKey("ArrowUp", Choices);
        Assert.True(state.IsChoiceHighlighted(0));
    }

    [Fact]
    public void Arrow_up_reaches_None_only_when_offered()
    {
        var addWorkspace = OpenPicker(allowNone: true, selected: 1);
        addWorkspace.HandleKey("ArrowUp", Choices);
        Assert.True(addWorkspace.IsNoneHighlighted);

        var restore = OpenPicker(allowNone: false, selected: 1);
        restore.HandleKey("ArrowUp", Choices);
        Assert.False(restore.IsNoneHighlighted);
        Assert.True(restore.IsChoiceHighlighted(0));
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData("NumpadEnter")]
    public void Enter_selects_the_highlighted_repository_and_closes(string key)
    {
        var state = OpenPicker(allowNone: false);
        state.SetFilter(Choices, "beta");

        var selection = state.HandleKey(key, Choices);

        Assert.Equal(new RepositoryPickerSelection(2), selection);
        Assert.False(state.IsOpen);
    }

    [Fact]
    public void Enter_on_None_selects_no_repository()
    {
        var state = OpenPicker(allowNone: true, selected: 2);
        state.HandleKey("ArrowUp", Choices);
        state.HandleKey("ArrowUp", Choices);

        Assert.Equal(new RepositoryPickerSelection(null), state.HandleKey("Enter", Choices));
    }

    [Fact]
    public void Escape_closes_without_selecting()
    {
        var state = OpenPicker(allowNone: false, selected: 1);
        state.HandleKey("ArrowDown", Choices);

        Assert.Null(state.HandleKey("Escape", Choices));
        Assert.False(state.IsOpen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_matches_shows_nothing_and_Enter_does_nothing(bool allowNone)
    {
        var state = OpenPicker(allowNone);
        state.SetFilter(Choices, "nothing-like-this");

        Assert.Empty(state.Filtered(Choices));
        Assert.Null(state.HandleKey("Enter", Choices));
        Assert.True(state.IsOpen);
    }

    [Fact]
    public void Without_None_an_empty_catalog_selects_nothing()
    {
        var state = new RepositoryPickerState { AllowNone = false };
        state.Open([], null);

        Assert.Null(state.HandleKey("Enter", []));
    }

    [Fact]
    public void Keys_are_ignored_while_closed()
    {
        var state = new RepositoryPickerState { AllowNone = true };

        Assert.Null(state.HandleKey("Enter", Choices));
        Assert.False(state.IsOpen);
    }

    [Fact]
    public void Restore_choices_feed_the_picker_with_connector_text_only_when_ambiguous()
    {
        var choices = RestoreWorkspaceFlow.ToPickerChoices(RestoreWorkspaceFlow.BuildChoices(
        [
            new RestoreRepositorySource(1, "GrayMoon.Workspace", "Jandini", "Personal GitHub"),
            new RestoreRepositorySource(2, "GrayMoon.Workspace", "Jandini", "Work GitHub"),
            new RestoreRepositorySource(3, "GrayMoon.Release", "Jandini", "Personal GitHub"),
        ]));

        Assert.Equal(
            ["Jandini/GrayMoon.Release", "Jandini/GrayMoon.Workspace (Personal GitHub)", "Jandini/GrayMoon.Workspace (Work GitHub)"],
            choices.Select(c => c.DisplayName).ToArray());

        var state = new RepositoryPickerState { AllowNone = false };
        state.Open(choices, null);
        state.SetFilter(choices, "work github");
        Assert.Equal([2], state.Filtered(choices).Select(c => c.RepositoryId));
    }
}
