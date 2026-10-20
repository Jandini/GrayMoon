using GrayMoon.App.Components.Pages;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceActionsLastRunTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FormatRunElapsed_Completed_IsUpdatedMinusStarted()
    {
        var end = Start.AddMinutes(4).AddSeconds(12);

        var text = WorkspaceActions.FormatRunElapsed(Start, end, "success", end.AddHours(2));

        Assert.Equal("4m 12s", text);
    }

    [Fact]
    public void FormatRunElapsed_Running_UsesNow()
    {
        var text = WorkspaceActions.FormatRunElapsed(Start, Start, "running", Start.AddSeconds(45));

        Assert.Equal("45s", text);
    }

    [Fact]
    public void FormatRunElapsed_DropsWholeMinutesAndHours()
    {
        Assert.Equal("2m", WorkspaceActions.FormatRunElapsed(Start, Start.AddMinutes(2), "failed", Start));
        Assert.Equal("1h", WorkspaceActions.FormatRunElapsed(Start, Start.AddHours(1), "success", Start));
        Assert.Equal("1h 5m", WorkspaceActions.FormatRunElapsed(Start, Start.AddHours(1).AddMinutes(5), "aborted", Start));
        Assert.Equal("2d 3h", WorkspaceActions.FormatRunElapsed(Start, Start.AddDays(2).AddHours(3), "success", Start));
    }

    [Fact]
    public void FormatRunElapsed_MissingOrNegative_IsNull()
    {
        Assert.Null(WorkspaceActions.FormatRunElapsed(null, Start, "success", Start));
        Assert.Null(WorkspaceActions.FormatRunElapsed(Start, null, "success", Start));
        Assert.Null(WorkspaceActions.FormatRunElapsed(Start, Start.AddMinutes(-1), "failed", Start));
    }
}
