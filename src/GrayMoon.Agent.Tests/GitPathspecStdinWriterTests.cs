using System.Text;
using GrayMoon.Agent.Services.GitChanges;

namespace GrayMoon.Agent.Tests;

public class GitPathspecStdinWriterTests
{
    [Fact]
    public void BuildNulDelimitedUtf8_joins_paths_with_nul_and_trailing_nul()
    {
        var bytes = GitPathspecStdinWriter.BuildNulDelimitedUtf8(["a.txt", "b.txt", "c.txt"]);

        Assert.Equal("a.txt\0b.txt\0c.txt\0", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void BuildNulDelimitedUtf8_returns_empty_for_no_paths()
    {
        var bytes = GitPathspecStdinWriter.BuildNulDelimitedUtf8([]);

        Assert.Empty(bytes);
    }

    [Fact]
    public void BuildNulDelimitedUtf8_preserves_spaces_and_unicode()
    {
        var bytes = GitPathspecStdinWriter.BuildNulDelimitedUtf8(["my file with spaces.txt", "文件.txt", "quo\"te.txt"]);

        Assert.Equal("my file with spaces.txt\0文件.txt\0quo\"te.txt\0", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void BuildBoundedBatches_keeps_a_single_batch_when_under_the_limit()
    {
        var batches = GitPathspecStdinWriter.BuildBoundedBatches(["a.txt", "b.txt"], maxBatchCharacters: 1000);

        var batch = Assert.Single(batches);
        Assert.Equal(["a.txt", "b.txt"], batch);
    }

    [Fact]
    public void BuildBoundedBatches_splits_when_over_the_limit()
    {
        var paths = Enumerable.Range(0, 1000).Select(i => $"file-{i}.txt").ToList();

        var batches = GitPathspecStdinWriter.BuildBoundedBatches(paths, maxBatchCharacters: 200);

        Assert.True(batches.Count > 1);
        Assert.Equal(paths.Count, batches.Sum(b => b.Count));
        Assert.Equal(paths, batches.SelectMany(b => b));
    }

    [Fact]
    public void BuildBoundedBatches_never_splits_within_a_batch_over_the_limit_for_a_single_long_path()
    {
        var longPath = new string('a', 500) + ".txt";

        var batches = GitPathspecStdinWriter.BuildBoundedBatches([longPath], maxBatchCharacters: 100);

        var batch = Assert.Single(batches);
        Assert.Equal([longPath], batch);
    }

    [Fact]
    public void BuildBoundedBatches_returns_no_batches_for_empty_input()
    {
        Assert.Empty(GitPathspecStdinWriter.BuildBoundedBatches([]));
    }

    [Fact]
    public void BuildBoundedBatches_handles_thousands_of_paths()
    {
        var paths = Enumerable.Range(0, 5000).Select(i => $"src/module{i % 50}/File{i}.cs").ToList();

        var batches = GitPathspecStdinWriter.BuildBoundedBatches(paths);

        Assert.Equal(paths.Count, batches.Sum(b => b.Count));
        Assert.All(batches, b => Assert.True(b.Sum(p => p.Length + 1) <= 20_000 || b.Count == 1));
    }

    [Theory]
    [InlineData("git version 2.43.0", true)]
    [InlineData("git version 2.25.0", true)]
    [InlineData("git version 2.25.1.windows.1", true)]
    [InlineData("git version 2.24.0", false)]
    [InlineData("git version 1.8.3.1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not a version string", false)]
    public void SupportsPathspecFromFile_compares_against_git_2_25(string? versionOutput, bool expected)
    {
        Assert.Equal(expected, GitPathspecStdinWriter.SupportsPathspecFromFile(versionOutput));
    }
}
