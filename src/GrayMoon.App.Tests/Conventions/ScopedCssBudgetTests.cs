using System.Text.RegularExpressions;

namespace GrayMoon.App.Tests.Conventions;

/// <summary>
/// Size budget for the scoped stylesheets (<c>*.razor.css</c>) of GrayMoon.App. Every one of these files is
/// rewritten by the SDK <c>RewriteCss</c> task on each build and ends up in <c>GrayMoon.App.styles.css</c>, which
/// every page downloads. The budget keeps a single file, and the total, from growing unnoticed. Rules shared by
/// several components belong in a global stylesheet under <c>wwwroot/css</c>, not copied into each component.
/// See docs/todo/css-optimization-report.md for the measurements behind the numbers.
/// </summary>
public sealed class ScopedCssBudgetTests
{
    /// <summary>Largest allowed <c>*.razor.css</c> file, in bytes.</summary>
    private const int MaxFileBytes = 36_000;

    /// <summary>Largest allowed number of selectors (comma-separated parts of style rules) in one file.</summary>
    private const int MaxFileSelectors = 240;

    /// <summary>Largest allowed total size of all <c>*.razor.css</c> files, in bytes.</summary>
    private const int MaxTotalBytes = 245_000;

    [Fact]
    public void EachScopedStylesheet_StaysWithinTheFileBudget()
    {
        var overBudget = ScopedStylesheets()
            .Select(f => (File: ConventionSource.Relative(f), Bytes: new FileInfo(f).Length, Selectors: CountSelectors(File.ReadAllText(f))))
            .Where(x => x.Bytes > MaxFileBytes || x.Selectors > MaxFileSelectors)
            .Select(x => $"{x.File}: {x.Bytes} bytes, {x.Selectors} selectors")
            .ToList();

        Assert.True(overBudget.Count == 0,
            $"Scoped stylesheets over the budget ({MaxFileBytes} bytes, {MaxFileSelectors} selectors per file). "
            + "Move rules that several components share to a global stylesheet in wwwroot/css, or split the component:"
            + Environment.NewLine + string.Join(Environment.NewLine, overBudget));
    }

    [Fact]
    public void AllScopedStylesheets_StayWithinTheTotalBudget()
    {
        var files = ScopedStylesheets().ToList();
        Assert.NotEmpty(files);
        var total = files.Sum(f => new FileInfo(f).Length);

        Assert.True(total <= MaxTotalBytes,
            $"The scoped stylesheets total {total} bytes, over the budget of {MaxTotalBytes}. Largest files:"
            + Environment.NewLine + string.Join(Environment.NewLine, files
                .OrderByDescending(f => new FileInfo(f).Length).Take(5)
                .Select(f => $"{ConventionSource.Relative(f)}: {new FileInfo(f).Length} bytes")));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData(".a { color: red; }", 1)]
    [InlineData(".a, .b:not(.c, .d) > .e { color: red; }", 2)]
    [InlineData("/* .x, .y { } */ .a { color: red; }", 1)]
    [InlineData("@media (max-width: 10px) { .a, .b { color: red; } .c { color: blue; } }", 3)]
    [InlineData("@keyframes spin { from, 50% { opacity: 0; } to { opacity: 1; } } .a { animation: spin 1s; }", 1)]
    [InlineData(".a[title=\"x,y\"] { color: red; }", 1)]
    public void CountSelectors_CountsStyleRuleSelectorsOnly(string css, int expected) =>
        Assert.Equal(expected, CountSelectors(css));

    private static IEnumerable<string> ScopedStylesheets() =>
        ConventionSource.Files(Path.Combine(ConventionSource.AppDirectory, "Components"), ".razor.css");

    /// <summary>
    /// Number of selectors in the style rules of <paramref name="css"/>: each comma-separated part of a rule's
    /// selector list counts once. Comments and <c>@keyframes</c> blocks are ignored; rules inside
    /// <c>@media</c> / <c>@supports</c> are counted.
    /// </summary>
    internal static int CountSelectors(string css)
    {
        css = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var count = 0;
        var preludeStart = 0;
        for (var i = 0; i < css.Length; i++)
        {
            var c = css[i];
            if (c == '}' || c == ';')
            {
                preludeStart = i + 1;
                continue;
            }

            if (c != '{')
                continue;

            var prelude = css[preludeStart..i].Trim();
            if (prelude.StartsWith('@'))
            {
                if (prelude.StartsWith("@media", StringComparison.OrdinalIgnoreCase)
                    || prelude.StartsWith("@supports", StringComparison.OrdinalIgnoreCase))
                {
                    preludeStart = i + 1; // the rules inside are counted
                    continue;
                }

                i += ConventionSource.Balanced(css, i, '{', '}').Length - 1; // @keyframes, @font-face: skipped
            }
            else
            {
                count += TopLevelParts(prelude);
                i += ConventionSource.Balanced(css, i, '{', '}').Length - 1;
            }

            preludeStart = i + 1;
        }

        return count;
    }

    /// <summary>Comma-separated parts of a selector list, ignoring commas inside parentheses, brackets and strings.</summary>
    private static int TopLevelParts(string selectorList)
    {
        if (selectorList.Length == 0)
            return 0;
        var parts = 1;
        var depth = 0;
        var quote = '\0';
        foreach (var c in selectorList)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
            }
            else if (c is '"' or '\'')
                quote = c;
            else if (c is '(' or '[')
                depth++;
            else if (c is ')' or ']')
                depth--;
            else if (c == ',' && depth == 0)
                parts++;
        }

        return parts;
    }
}
