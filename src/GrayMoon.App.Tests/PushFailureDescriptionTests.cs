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
    public void A_level_zero_failure_has_no_level_in_the_message()
    {
        var failed = OperationResult.Fail("x", null, new Dictionary<int, string> { [0] = "Worker not connected." });

        Assert.Equal("Push stopped: Worker not connected.", PushOperationResult.Describe(failed));
    }
}
