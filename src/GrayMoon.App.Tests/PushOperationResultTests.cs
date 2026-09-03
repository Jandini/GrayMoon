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
    public void FromErrors_with_level_errors_keeps_them_off_repos()
    {
        var levelErrors = new Dictionary<int, string>
        {
            [2] = "Timed out waiting for package dependencies after 6 min (1 of 3 found).",
        };

        var result = PushOperationResult.FromErrors(new Dictionary<int, string>(), levelErrors);

        Assert.False(result.Success);
        Assert.Equal(PushOperationResult.StoppedAfterFailures, result.Error);
        Assert.Null(result.RepoErrors);
        Assert.NotNull(result.LevelErrors);
        Assert.Equal(levelErrors[2], result.LevelErrors[2]);
    }

    [Fact]
    public void FromErrors_with_repo_and_level_maps_both()
    {
        var repoErrors = new Dictionary<int, string> { [7] = "push rejected" };
        var levelErrors = new Dictionary<int, string> { [0] = "workspace not found" };

        var result = PushOperationResult.FromErrors(repoErrors, levelErrors);

        Assert.False(result.Success);
        Assert.Equal("push rejected", result.RepoErrors![7]);
        Assert.Equal("workspace not found", result.LevelErrors![0]);
    }

    [Fact]
    public void CanProceedToNextLevel_is_false_when_any_push_failed()
    {
        Assert.True(PushOperationResult.CanProceedToNextLevel(0));
        Assert.False(PushOperationResult.CanProceedToNextLevel(1));
        Assert.False(PushOperationResult.CanProceedToNextLevel(3));
    }
}
