using AngleSharp.Html.Parser;
using GrayMoon.App.Services.GitChanges;

namespace GrayMoon.App.Tests;

/// <summary>
/// Regression for Git Changes -> Preview -> Changes corrupting inline code / bold that holds a
/// <c>{PLACEHOLDER}</c> (heading cut after the backtick, literal &amp;#x20; text in the paragraph).
/// </summary>
public sealed class MarkdownPlaceholderPreviewTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Placeholder = "{STAGE_NUMBER}";

    private readonly MarkdownProseDiffService _sut = new();

    private static string LoadFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Markdown", "stage-placeholder-preview.md"))
            .Replace("\r\n", "\n");

    private static string VisibleText(string? html)
    {
        var doc = new HtmlParser().ParseDocument($"<body>{html}</body>");
        return doc.Body!.TextContent;
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private void Dump(string label, string? html)
    {
        output.WriteLine($"=== {label} ===");
        output.WriteLine("HTML: " + html);
        output.WriteLine("TEXT: " + VisibleText(html));
    }

    public static TheoryData<string, string> Pairs()
    {
        var original = LoadFixture();
        return new TheoryData<string, string>
        {
            { original, original },
            { original, original + "\nA trailing sentence added after the fixture.\n" },
            { original, original.Replace("only responsible", "solely responsible") },
        };
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Changes_keeps_both_placeholders_and_no_literal_entities(string before, string after)
    {
        var result = _sut.Render(before, after, MarkdownProsePreviewMode.Changes);

        Assert.True(result.Success);
        Dump("Changes", result.Html);
        var text = VisibleText(result.Html);
        Assert.Equal(2, Count(text, Placeholder));
        Assert.DoesNotContain("&#x", text);
        Assert.DoesNotContain("&amp;", text);
        Assert.Contains("copy everything below the line", text);
        Assert.Contains("Stage " + Placeholder, text);
    }

    [Theory]
    [InlineData("Use `{X}` and `${X}` and `{{X}}` here.", "Use `{X}` and `${X}` and `{{X}}` here, edited.")]
    [InlineData("Plain {X} braces **bold {Y}** (see {Z}).", "Plain {X} braces **bold {Y}** (see {Z}). More.")]
    [InlineData("Literal <PackageVersion> and &amp; and &#x20; text.", "Literal <PackageVersion> and &amp; and &#x20; text. More.")]
    public void Changes_keeps_brace_tokens_and_marks_real_edits(string before, string after)
    {
        var result = _sut.Render(before, after, MarkdownProsePreviewMode.Changes);

        Assert.True(result.Success);
        var text = VisibleText(result.Html);
        foreach (var token in new[] { "{X}", "{Y}", "{Z}", "<PackageVersion>" }.Where(before.Contains))
        {
            Assert.Contains(token, text);
        }

        Assert.Contains("<ins", result.Html);
        Assert.DoesNotContain("<script", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(MarkdownProsePreviewMode.After)]
    [InlineData(MarkdownProsePreviewMode.Before)]
    public void Single_side_modes_keep_both_placeholders(MarkdownProsePreviewMode mode)
    {
        var fixture = LoadFixture();
        var result = _sut.Render(fixture, fixture, mode);

        Dump(mode.ToString(), result.Html);
        var text = VisibleText(result.Html);
        Assert.Equal(2, Count(text, Placeholder));
        Assert.DoesNotContain("&#x", text);
    }
}
