using GrayMoon.App.Services.GitChanges;

namespace GrayMoon.App.Tests;

public sealed class GitChangesRestoredSelectionTests
{
    [Fact]
    public void A_restored_file_whose_diff_failed_is_dropped()
    {
        Assert.True(GitChangesRestoredSelection.ShouldDrop(superseded: false, diffFailed: true));
    }

    [Fact]
    public void A_restored_file_whose_diff_loaded_stays_selected()
    {
        Assert.False(GitChangesRestoredSelection.ShouldDrop(superseded: false, diffFailed: false));
    }

    // The user clicked another file while the restored diff was loading. The restored request ends without a
    // document (it was superseded) while the user's diff is still loading; the old check read that as a failure
    // and cleared the user's selection.
    [Fact]
    public void A_file_the_user_picked_while_the_restored_diff_was_loading_is_never_dropped()
    {
        Assert.False(GitChangesRestoredSelection.ShouldDrop(superseded: true, diffFailed: true));
        Assert.False(GitChangesRestoredSelection.ShouldDrop(superseded: true, diffFailed: false));
    }
}
