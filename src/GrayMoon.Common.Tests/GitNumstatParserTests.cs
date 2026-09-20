using GrayMoon.Common.Git;

namespace GrayMoon.Common.Tests;

public class GitNumstatParserTests
{
    [Fact]
    public void Empty_or_null_output_is_zero()
    {
        Assert.Equal(new GitNumstatTotals(0, 0), GitNumstatParser.Parse(null));
        Assert.Equal(new GitNumstatTotals(0, 0), GitNumstatParser.Parse(""));
        Assert.Equal(new GitNumstatTotals(0, 0), GitNumstatParser.Parse("\n"));
    }

    [Fact]
    public void Sums_added_and_deleted_columns()
    {
        var output = "10\t2\tsrc/a.cs\n3\t0\tsrc/b.cs\n";

        var totals = GitNumstatParser.Parse(output);

        Assert.Equal(13, totals.Insertions);
        Assert.Equal(2, totals.Deletions);
    }

    [Fact]
    public void Skips_binary_dash_rows()
    {
        var output = "1\t1\tfile.cs\n-\t-\timage.png\n";

        var totals = GitNumstatParser.Parse(output);

        Assert.Equal(1, totals.Insertions);
        Assert.Equal(1, totals.Deletions);
    }

    [Fact]
    public void Accepts_rename_paths()
    {
        var output = "4\t1\told.cs => new.cs\n";

        var totals = GitNumstatParser.Parse(output);

        Assert.Equal(4, totals.Insertions);
        Assert.Equal(1, totals.Deletions);
    }

    [Fact]
    public void Skips_malformed_lines()
    {
        var output = "not a numstat line\n5\t2\tok.cs\n";

        var totals = GitNumstatParser.Parse(output);

        Assert.Equal(5, totals.Insertions);
        Assert.Equal(2, totals.Deletions);
    }

    [Fact]
    public void Accepts_crlf_lines()
    {
        var output = "2\t3\ta.txt\r\n1\t0\tb.txt\r\n";

        var totals = GitNumstatParser.Parse(output);

        Assert.Equal(3, totals.Insertions);
        Assert.Equal(3, totals.Deletions);
    }
}
