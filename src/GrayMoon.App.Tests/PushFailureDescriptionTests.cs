using GrayMoon.App.Services.Orchestration;
using GrayMoon.Application;

namespace GrayMoon.App.Tests;

public sealed class PushFailureDescriptionTests
{
    [Fact]
    public void A_level_failure_names_the_lowest_failed_level_and_its_error()
    {
        var failed = PushOperationResult.FromErrors(
            new Dictionary<int, string>(),
            new Dictionary<int, string>
            {
                [4] = "later",
                [2] = "Timed out waiting for package dependencies after 10 min (1 of 3 found).",
            });

        Assert.Equal(
            "Push stopped at level 2: Timed out waiting for package dependencies after 10 min (1 of 3 found).",
            PushOperationResult.Describe(failed));
    }

    [Fact]
    public void A_failure_without_a_level_error_uses_the_result_error()
    {
        var failed = PushOperationResult.FromErrors(new Dictionary<int, string> { [7] = "rejected" }, new Dictionary<int, string>());

        Assert.Equal(PushOperationResult.StoppedAfterFailures, PushOperationResult.Describe(failed));
    }

    [Fact]
    public void A_push_whose_only_problem_is_a_failed_restore_is_done_with_a_warning()
    {
        var result = PushOperationResult.FromRepoErrors(new Dictionary<int, string>
        {
            [7] = "Package restore failed for repo-7: error NU1101",
            [3] = "Package restore failed for repo-3: timed out",
        });

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.True(result.HasWarnings);
        Assert.Equal(
            ["Package restore failed for repo-3: timed out", "Package restore failed for repo-7: error NU1101"],
            result.Warnings);
        Assert.Equal("Push finished, but package restore failed in 2 repositories.", PushOperationResult.DescribeWarnings(result));
        // The warning stays on its repository for the grid.
        Assert.Equal("Package restore failed for repo-7: error NU1101", result.RepoErrors![7]);

        // Reported once as a warning, never as an error and never dropped.
        var errors = new List<string>();
        var warnings = new List<string>();
        result.ShowRepoErrors(errors.Add, warnings.Add);
        Assert.Empty(errors);
        Assert.Equal(["Push finished, but package restore failed in 2 repositories."], warnings);

        var fallback = new List<string>();
        result.ShowRepoErrors(fallback.Add);
        Assert.Single(fallback);
    }

    [Fact]
    public void A_restore_warning_next_to_a_real_failure_is_still_a_failed_push()
    {
        var mixed = PushOperationResult.FromRepoErrors(new Dictionary<int, string>
        {
            [7] = "Package restore failed for repo-7: error NU1101",
            [8] = "rejected",
        });
        Assert.False(mixed.Success);
        Assert.False(mixed.HasWarnings);

        var levelFailed = PushOperationResult.FromErrors(
            new Dictionary<int, string> { [7] = "Package restore failed for repo-7: error NU1101" },
            new Dictionary<int, string> { [2] = "timed out" });
        Assert.False(levelFailed.Success);
        Assert.Null(PushOperationResult.DescribeWarnings(levelFailed));
    }

    [Fact]
    public void A_level_zero_failure_has_no_level_in_the_message()
    {
        var failed = OperationResult.Fail("x", null, new Dictionary<int, string> { [0] = "Worker not connected." });

        Assert.Equal("Push stopped: Worker not connected.", PushOperationResult.Describe(failed));
    }
}
