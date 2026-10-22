using GrayMoon.App.Services.Orchestration;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceOperationOutcomeStoreTests
{
    private static readonly WorkspaceFeatureContextId Context = new(7);

    [Fact]
    public void Get_returns_recorded_level_error_after_the_run()
    {
        var store = new WorkspaceOperationOutcomeStore();

        store.Begin(1, Context, "Push").Level(3, "Timed out waiting for package dependencies");

        var outcome = store.Get(1, Context);
        Assert.NotNull(outcome);
        Assert.Contains("Level 3: Timed out", outcome!.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Begin_forgets_the_previous_outcome()
    {
        var store = new WorkspaceOperationOutcomeStore();
        store.Begin(1, Context, "Push").Level(3, "boom");

        store.Begin(1, Context, "Push");

        Assert.Null(store.Get(1, Context));
    }

    [Fact]
    public void Clear_removes_the_outcome()
    {
        var store = new WorkspaceOperationOutcomeStore();
        store.Begin(1, Context, "Update").Repository(5, "failed");

        store.Clear(1, Context);

        Assert.Null(store.Get(1, Context));
    }
}