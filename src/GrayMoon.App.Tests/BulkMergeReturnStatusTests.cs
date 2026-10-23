using GrayMoon.App.Components.Modals;

namespace GrayMoon.App.Tests;

public sealed class BulkMergeReturnStatusTests
{
    [Fact]
    public void Apply_marks_success_failure_and_leaves_unmutated_cancellation_for_the_finalizer()
    {
        var rows = new[]
        {
            Row(1, BulkMergeRowStatus.ReturningToDefault),
            Row(2, BulkMergeRowStatus.ReturningToDefault),
            Row(3, BulkMergeRowStatus.ReturningToDefault),
        };
        var cleared = new List<int>();
        var errors = new List<(int Id, string Message)>();

        BulkMergeReturnStatus.Apply(
            rows,
            [
                new UnattendedReturnToDefaultRepositoryResult(1, true, null, UnattendedReturnFailureStage.None, true, true),
                new UnattendedReturnToDefaultRepositoryResult(2, false, "dirty", UnattendedReturnFailureStage.Eligibility, false, false),
                new UnattendedReturnToDefaultRepositoryResult(3, false, UnattendedReturnToDefaultBatchResult.CancelledMessage, UnattendedReturnFailureStage.Cancelled, false, false),
            ],
            cleared.Add,
            (id, message) => errors.Add((id, message)));

        Assert.Equal(BulkMergeRowStatus.ReturnedToDefault, rows[0].Status);
        Assert.Null(rows[0].ErrorMessage);
        Assert.Equal([1], cleared);
        Assert.Equal(BulkMergeRowStatus.ReturnFailed, rows[1].Status);
        Assert.Equal("dirty", rows[1].ErrorMessage);
        Assert.Equal([(2, "dirty")], errors);
        Assert.Equal(BulkMergeRowStatus.ReturningToDefault, rows[2].Status);
    }

    [Fact]
    public void Finalizer_marks_unattempted_merges_skipped_and_interrupted_returns_cancelled()
    {
        var rows = new[]
        {
            Row(1, BulkMergeRowStatus.Merging),
            Row(2, BulkMergeRowStatus.ReturningToDefault),
            Row(3, BulkMergeRowStatus.ReturnedToDefault),
            Row(4, BulkMergeRowStatus.Failed),
        };

        BulkMergeRunFinalizer.FinalizeInterruptedRows(rows);
        var counts = BulkMergeRunFinalizer.Count(rows);

        Assert.Equal(BulkMergeRowStatus.Skipped, rows[0].Status);
        Assert.Equal(BulkMergeRowStatus.ReturnFailed, rows[1].Status);
        Assert.Equal(UnattendedReturnToDefaultBatchResult.CancelledMessage, rows[1].ErrorMessage);
        Assert.Equal(BulkMergeRowStatus.ReturnedToDefault, rows[2].Status);
        Assert.Equal(1, counts.Failed);
        Assert.Equal(1, counts.ReturnFailed);
        Assert.Equal(1, counts.Skipped);
        Assert.Equal(1, counts.Succeeded);
        Assert.Equal(0, counts.RealReturnFailed);
    }

    [Fact]
    public void Count_excludes_no_open_pr_companion_rows_from_succeeded_merges()
    {
        var rows = new[]
        {
            Row(1, BulkMergeRowStatus.ReturnedToDefault),
            NoOpenPrRow(2, BulkMergeRowStatus.ReturnedToDefault),
            NoOpenPrRow(3, BulkMergeRowStatus.ReturnFailed),
        };

        var counts = BulkMergeRunFinalizer.Count(rows);

        Assert.Equal(1, counts.Succeeded);
        Assert.Equal(1, counts.ReturnFailed);
        Assert.Equal(1, counts.RealReturnFailed);
    }

    private static BulkMergePrRow Row(int repositoryId, BulkMergeRowStatus status) => new()
    {
        RepositoryId = repositoryId,
        RepositoryName = $"repo-{repositoryId}",
        PrNumber = repositoryId,
        Status = status,
    };

    private static BulkMergePrRow NoOpenPrRow(int repositoryId, BulkMergeRowStatus status) => new()
    {
        RepositoryId = repositoryId,
        RepositoryName = $"repo-{repositoryId}",
        PrNumber = 0,
        HasNoOpenPullRequest = true,
        Status = status,
        ErrorMessage = status == BulkMergeRowStatus.ReturnFailed ? "dirty" : null,
    };
}
