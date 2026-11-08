using GrayMoon.App.Services.GitChanges;

namespace GrayMoon.App.Tests;

/// <summary>
/// A multi-repository commit or stage must account for every repository it set out to handle. A repository with no
/// recorded result is reported as failed, never silently dropped.
/// </summary>
public sealed class RepositoryOutcomesTests
{
    private static RepositoryOutcomes ThreeRepositories() =>
        new([(1, "Edx1.A"), (2, "Edx1.B"), (3, "Edx1.C")]);

    [Fact]
    public void All_succeeded_is_a_plain_count()
    {
        var outcomes = ThreeRepositories();
        outcomes.Succeeded(1, "Edx1.A");
        outcomes.Succeeded(2, "Edx1.B");
        outcomes.Succeeded(3, "Edx1.C");

        var results = outcomes.Complete();

        Assert.False(RepositoryOutcomes.HasFailures(results));
        Assert.Equal("Committed in 3 repositories.", RepositoryOutcomes.Summarize(results, "Committed", "Not committed"));
    }

    [Fact]
    public void A_repository_with_no_recorded_result_is_reported_as_failed()
    {
        var outcomes = ThreeRepositories();
        outcomes.Succeeded(1, "Edx1.A");
        outcomes.Succeeded(3, "Edx1.C");

        var results = outcomes.Complete();

        Assert.True(RepositoryOutcomes.HasFailures(results));
        Assert.Equal(3, results.Count);
        var missing = Assert.Single(results, r => r.Kind == RepositoryOutcomeKind.Failed);
        Assert.Equal("Edx1.B", missing.Repository);
        Assert.Contains("No result was recorded", missing.Detail);
    }

    [Fact]
    public void The_summary_names_each_failed_repository_with_its_reason()
    {
        var outcomes = ThreeRepositories();
        outcomes.Succeeded(1, "Edx1.A");
        outcomes.Failed(2, "Edx1.B", "Unexpected error (InvalidOperationException) - see logs.");
        outcomes.Succeeded(3, "Edx1.C");

        var summary = RepositoryOutcomes.Summarize(outcomes.Complete(), "Committed", "Not committed");

        Assert.Equal(
            "Committed in 2 of 3 repositories. Not committed (1): Edx1.B (Unexpected error (InvalidOperationException) - see logs.).",
            summary);
    }

    [Fact]
    public void Skipped_repositories_are_named_but_are_not_failures()
    {
        var outcomes = ThreeRepositories();
        outcomes.Succeeded(1, "Edx1.A");
        outcomes.Skipped(2, "Edx1.B", "no changes to commit");
        outcomes.Succeeded(3, "Edx1.C");

        var results = outcomes.Complete();

        Assert.False(RepositoryOutcomes.HasFailures(results));
        Assert.Equal(
            "Committed in 2 of 3 repositories. Nothing to do in (1): Edx1.B.",
            RepositoryOutcomes.Summarize(results, "Committed", "Not committed"));
    }

    [Fact]
    public void The_first_recorded_outcome_wins()
    {
        var outcomes = new RepositoryOutcomes([(1, "Edx1.A")]);
        outcomes.Failed(1, "Edx1.A", "first");
        outcomes.Succeeded(1, "Edx1.A");

        var only = Assert.Single(outcomes.Complete());
        Assert.Equal(RepositoryOutcomeKind.Failed, only.Kind);
    }

    [Fact]
    public void A_long_list_of_failures_is_shortened()
    {
        var expected = Enumerable.Range(1, 10).Select(i => (i, $"Edx1.R{i}")).ToList();
        var outcomes = new RepositoryOutcomes(expected);
        foreach (var (key, name) in expected)
            outcomes.Failed(key, name, "boom");

        var summary = RepositoryOutcomes.Summarize(outcomes.Complete(), "Staged", "Not staged");

        Assert.StartsWith("Staged in 0 of 10 repositories. Not staged (10): Edx1.R1 (boom)", summary);
        Assert.Contains("and 4 more", summary);
    }

    [Fact]
    public async Task Recording_from_many_threads_keeps_every_repository()
    {
        var expected = Enumerable.Range(1, 200).Select(i => (i, $"Edx1.R{i}")).ToList();
        var outcomes = new RepositoryOutcomes(expected);

        await Task.WhenAll(expected.Select(e => Task.Run(() => outcomes.Succeeded(e.i, e.Item2))));

        var results = outcomes.Complete();
        Assert.Equal(200, results.Count);
        Assert.All(results, r => Assert.Equal(RepositoryOutcomeKind.Succeeded, r.Kind));
    }
}
