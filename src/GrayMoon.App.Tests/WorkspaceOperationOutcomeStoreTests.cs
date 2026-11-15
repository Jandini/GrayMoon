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
    public void Describe_names_the_failed_repositories_with_their_error_instead_of_only_counting_them()
    {
        var store = new WorkspaceOperationOutcomeStore();
        var recorder = store.Begin(1, Context, "Update and Push");
        recorder.Level(1, "Push stopped after repository failure(s).");
        recorder.Repository(27, "The repository is not on a branch (detached HEAD).\nsecond line");
        recorder.Repository(26, "The repository is not on a branch (detached HEAD).");
        var names = new Dictionary<int, string> { [26] = "Partition", [27] = "Metrics" };

        var text = store.Get(1, Context)!.Describe(id => names.GetValueOrDefault(id));

        Assert.Equal(
            "Update and Push stopped. Level 1: Push stopped after repository failure(s).\n"
            + "Metrics: The repository is not on a branch (detached HEAD).\n"
            + "Partition: The repository is not on a branch (detached HEAD).",
            text);
    }

    [Fact]
    public void Describe_counts_the_repositories_it_cannot_name_or_has_no_room_for()
    {
        var store = new WorkspaceOperationOutcomeStore();
        var recorder = store.Begin(1, Context, "Push");
        for (var id = 1; id <= 5; id++)
            recorder.Repository(id, "failed");

        var outcome = store.Get(1, Context)!;

        Assert.Equal("Push stopped. 5 repositories reported errors.", outcome.Describe());
        Assert.EndsWith("repo-3: failed\n2 more repositories reported errors.", outcome.Describe(id => "repo-" + id), StringComparison.Ordinal);
    }

    [Fact]
    public void Begin_forgets_the_previous_outcome()
    {
        var store = new WorkspaceOperationOutcomeStore();
        store.Begin(1, Context, "Push").Level(3, "boom");

        store.Begin(1, Context, "Push");

        Assert.Null(store.Get(1, Context));
    }

    // Characterizes the current behavior behind finding F07: the Update and Push facades each call Begin, so a Push
    // that starts after a failed Update replaces the Update failure. The combined entry point must own one outcome
    // for the whole run; when it does, replace this with a test of that behavior.
    [Fact]
    public void Begin_for_a_following_push_replaces_an_earlier_update_failure()
    {
        var store = new WorkspaceOperationOutcomeStore();
        store.Begin(1, Context, "Update").Level(2, "Update failed");

        store.Begin(1, Context, "Push");

        Assert.Null(store.Get(1, Context));
    }

    [Fact]
    public void Begin_with_the_run_id_of_the_current_run_joins_it_and_keeps_the_earlier_failure()
    {
        var store = new WorkspaceOperationOutcomeStore();
        store.Begin(1, Context, "Update and Push", "run-1").Level(2, "Update failed");

        store.Begin(1, Context, "Push", "run-1").Repository(9, "push failed");

        var outcome = store.Get(1, Context);
        Assert.NotNull(outcome);
        Assert.Equal("Update and Push", outcome!.Operation);
        Assert.Equal("Update failed", outcome.LevelErrors[2]);
        Assert.Equal("push failed", outcome.RepositoryErrors[9]);
    }

    [Fact]
    public void Begin_with_a_different_run_id_starts_a_new_outcome()
    {
        var store = new WorkspaceOperationOutcomeStore();
        store.Begin(1, Context, "Update and Push", "run-1").Level(2, "Update failed");

        store.Begin(1, Context, "Update and Push", "run-2");

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