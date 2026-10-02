using GrayMoon.App.Components.Features;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Tests;

/// <summary>E5: BranchExists create failures carry structured collisions for the dialog layout.</summary>
public sealed class CreateFeatureBranchExistsDialogTests
{
    [Fact]
    public void FormatBranchExistsSummary_includes_x_of_y()
    {
        var summary = CreateFeatureModal.FormatBranchExistsSummary("my-feat", collisionCount: 3, totalRepositoryCount: 11);

        Assert.Equal("Branch 'my-feat' already exists in 3 of 11 repositories", summary);
        Assert.DoesNotContain(";", summary);
    }

    [Fact]
    public void CreateFeatureResult_BranchExists_carries_collisions()
    {
        var result = new CreateFeatureResult
        {
            Success = false,
            Condition = "BranchExists",
            Error = "Branch 'x' already exists in 2 of 5 repositories.",
            TotalRepositoryCount = 5,
            BranchCollisions =
            [
                new CreateFeatureBranchCollision { RepositoryName = "RepoA", Refs = ["origin/x"] },
                new CreateFeatureBranchCollision { RepositoryName = "RepoB", Refs = ["x", "origin/x"] }
            ]
        };

        Assert.Equal(2, result.BranchCollisions!.Count);
        Assert.Equal("RepoA", result.BranchCollisions[0].RepositoryName);
        Assert.Equal(5, result.TotalRepositoryCount);
        Assert.Equal(
            "Branch 'x' already exists in 2 of 5 repositories",
            CreateFeatureModal.FormatBranchExistsSummary("x", result.BranchCollisions.Count, result.TotalRepositoryCount!.Value));
    }
}
