using GrayMoon.App.Services.Orchestration;

namespace GrayMoon.App.Tests;

public sealed class DependencyCommitResultsTests
{
    [Fact]
    public void Synced_repo_that_did_not_commit_is_an_error()
    {
        var classified = DependencyCommitResults.Classify(
        [
            (RepoId: 1, Committed: false, ErrorMessage: null)
        ],
        requireCommittedRepoIds: new HashSet<int> { 1 });

        var error = Assert.Single(classified.Errors);
        Assert.Equal(1, error.RepoId);
        Assert.Equal(DependencyCommitResults.NotCommittedError, error.Error);
        Assert.Empty(classified.CommittedRepoIds);
    }

    [Fact]
    public void One_repo_commit_error_does_not_drop_other_repos_errors()
    {
        var classified = DependencyCommitResults.Classify(
        [
            (RepoId: 1, Committed: false, ErrorMessage: "index.lock"),
            (RepoId: 2, Committed: false, ErrorMessage: null),
            (RepoId: 3, Committed: true, ErrorMessage: null)
        ],
        requireCommittedRepoIds: new HashSet<int> { 1, 2, 3 });

        Assert.Equal(2, classified.Errors.Count);
        Assert.Contains(classified.Errors, e => e.RepoId == 1 && e.Error == "index.lock");
        Assert.Contains(classified.Errors, e => e.RepoId == 2 && e.Error == DependencyCommitResults.NotCommittedError);
        Assert.Equal([3], classified.CommittedRepoIds);
    }
}
