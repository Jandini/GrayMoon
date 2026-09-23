using GrayMoon.App.Services.GitChanges;

namespace GrayMoon.App.Tests;

public sealed class MarkdownProseDiffServiceTests
{
    private readonly MarkdownProseDiffService _sut = new();

    [Fact]
    public void After_renders_heading_and_emphasis()
    {
        var result = _sut.Render("# Title\n\nHello **world**", "# Title\n\nHello **world**", MarkdownProsePreviewMode.After);

        Assert.True(result.Success);
        Assert.Contains("<h1", result.Html);
        Assert.Contains("<strong>world</strong>", result.Html);
        Assert.DoesNotContain("<ins", result.Html);
    }

    [Fact]
    public void Changes_highlights_word_edit()
    {
        var result = _sut.Render("Hello old world", "Hello new world", MarkdownProsePreviewMode.Changes);

        Assert.True(result.Success);
        Assert.Contains("<del", result.Html);
        Assert.Contains("<ins", result.Html);
        Assert.Contains("old", result.Html);
        Assert.Contains("new", result.Html);
    }

    [Fact]
    public void Changes_preserves_table()
    {
        const string table = "| A | B |\n| --- | --- |\n| 1 | 2 |\n";
        var result = _sut.Render(table, table.Replace("2", "3"), MarkdownProsePreviewMode.Changes);

        Assert.True(result.Success);
        Assert.Contains("<table", result.Html);
    }

    [Fact]
    public void New_file_wraps_as_all_inserts()
    {
        var result = _sut.Render(null, "# Brand new", MarkdownProsePreviewMode.Changes);

        Assert.True(result.Success);
        Assert.Contains("markdown-diff-all-ins", result.Html);
        Assert.Contains("<h1", result.Html);
    }

    [Fact]
    public void Deleted_file_wraps_as_all_deletes()
    {
        var result = _sut.Render("# Gone", null, MarkdownProsePreviewMode.Changes);

        Assert.True(result.Success);
        Assert.Contains("markdown-diff-all-del", result.Html);
        Assert.Contains("<h1", result.Html);
    }

    [Fact]
    public void Empty_both_sides_returns_empty_html()
    {
        var result = _sut.Render("", "", MarkdownProsePreviewMode.After);

        Assert.True(result.Success);
        Assert.True(string.IsNullOrEmpty(result.Html));
    }

    [Fact]
    public void Strips_script_payload_from_markdown_html_attempt()
    {
        // DisableHtml means raw HTML in the source is not parsed as tags; still assert no script survives.
        var result = _sut.Render(
            "Safe",
            "Safe <script>alert(1)</script>",
            MarkdownProsePreviewMode.After);

        Assert.True(result.Success);
        Assert.DoesNotContain("<script", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mermaid_fence_emitted_as_pre_mermaid()
    {
        const string md = "```mermaid\nflowchart LR\n  A --> B\n```\n";
        var result = _sut.Render(md, md, MarkdownProsePreviewMode.After);

        Assert.True(result.Success);
        Assert.Contains("class=\"mermaid\"", result.Html);
        Assert.Contains("flowchart LR", result.Html);
        Assert.DoesNotContain("language-mermaid", result.Html);
    }

    [Fact]
    public void Too_large_returns_too_large_flag()
    {
        var huge = new string('x', MarkdownProseDiffService.SoftCharLimitPerSide + 1);
        var result = _sut.Render(huge, "small", MarkdownProsePreviewMode.Changes);

        Assert.False(result.Success);
        Assert.True(result.TooLarge);
        Assert.Contains("too large", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Before_uses_original_only()
    {
        var result = _sut.Render("# Before", "# After", MarkdownProsePreviewMode.Before);

        Assert.True(result.Success);
        Assert.Contains("Before", result.Html);
        Assert.DoesNotContain("After", result.Html);
    }
}
