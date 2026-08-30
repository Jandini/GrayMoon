using GrayMoon.App.Services.Orchestration;

namespace GrayMoon.App.Tests;

public sealed class PushOperationResultTests
{
    [Fact]
    public void FromRepoErrors_empty_is_success()
    {
        var result = PushOperationResult.FromRepoErrors(new Dictionary<int, string>());

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Null(result.RepoErrors);
    }

    [Fact]
    public void FromRepoErrors_with_failures_is_fail_and_keeps_per_repo_errors()
    {
        var errors = new Dictionary<int, string>
        {
            [42] = "Push rejected: this repository is archived on GitHub and is read-only.",
        };

        var result = PushOperationResult.FromRepoErrors(errors);

        Assert.False(result.Success);
        Assert.Equal(PushOperationResult.StoppedAfterFailures, result.Error);
        Assert.NotNull(result.RepoErrors);
        Assert.Equal(errors[42], result.RepoErrors[42]);
    }

    [Fact]
    public void CanProceedToNextLevel_is_false_when_any_push_failed()
    {
        Assert.True(PushOperationResult.CanProceedToNextLevel(0));
        Assert.False(PushOperationResult.CanProceedToNextLevel(1));
        Assert.False(PushOperationResult.CanProceedToNextLevel(3));
    }
}
