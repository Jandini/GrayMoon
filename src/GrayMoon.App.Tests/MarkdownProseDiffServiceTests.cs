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

    [Fact]
    public void Changes_preserves_unicode_box_drawing()
    {
        // Regression: HtmlDiff must not turn U+251C (├) into Windows-1252 mojibake (â"œ).
        var oldMd = "```\nRoot\n\u251C\u2500\u2500 child\n```\n";
        var newMd = "Root responsibilities:\n\n- child\n";
        var result = _sut.Render(oldMd, newMd, MarkdownProsePreviewMode.Changes);

        Assert.True(result.Success);
        Assert.NotNull(result.Html);
        Assert.DoesNotContain("\u00E2", result.Html);
        Assert.True(
            result.Html.Contains('\u251C') || result.Html.Contains("child", StringComparison.Ordinal),
            "Expected box drawing or the shared word 'child' to survive the diff.");
    }

    [Fact]
    public void RepairUtf8Mojibake_restores_box_drawing_tee()
    {
        // â"œâ"€â"€ as committed in Desktop README HEAD~1 (UTF-8 of CP1252-misread ├──).
        var mojibake = "\u00E2\u201D\u0153\u00E2\u201D\u20AC\u00E2\u201D\u20AC starts App";
        var repaired = MarkdownProseDiffService.RepairUtf8Mojibake(mojibake);

        Assert.Contains("\u251C\u2500\u2500", repaired);
        Assert.DoesNotContain("\u00E2", repaired);
    }

    [Fact]
    public void Changes_repairs_mojibake_on_deleted_side()
    {
        var oldMd = "```\nGrayMoon.Desktop.exe\n\u00E2\u201D\u0153\u00E2\u201D\u20AC\u00E2\u201D\u20AC starts App\n```\n";
        var newMd = "Desktop responsibilities:\n\n- starts App\n";
        var result = _sut.Render(oldMd, newMd, MarkdownProsePreviewMode.Changes);

        Assert.True(result.Success);
        Assert.NotNull(result.Html);
        Assert.DoesNotContain("\u00E2\u201D\u0153", result.Html);
        Assert.Contains("starts App", result.Html);
    }

    [Fact]
    public void Changes_with_mermaid_and_code_fence_does_not_throw_overlapping_blocks()
    {
        const string oldMd = "```mermaid\nflowchart LR\n  A --> B\n```\n\n```\n\u251C\u2500\u2500 item\n```\n";
        const string newMd = "```mermaid\nflowchart LR\n  A --> C\n```\n\n```\n\u251C\u2500\u2500 item\n\u2514\u2500\u2500 other\n```\n";

        var result = _sut.Render(oldMd, newMd, MarkdownProsePreviewMode.Changes);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Html);
        Assert.Contains("mermaid", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains('\u251C', result.Html);
    }

    [Fact]
    public void Changes_marks_removed_text_with_del_diff_class()
    {
        var result = _sut.Render("Hello old world", "Hello new world", MarkdownProsePreviewMode.Changes);

        Assert.True(result.Success);
        Assert.NotNull(result.Html);
        Assert.Contains("diffmod", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<del", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("old", result.Html);
    }

    [Fact]
    public void Changes_marks_replaced_pre_block_with_del_wrapper()
    {
        var result = _sut.Render("```\nold line\n```\n", "```\nnew line\n```\n", MarkdownProsePreviewMode.Changes);

        Assert.True(result.Success);
        Assert.NotNull(result.Html);
        Assert.Contains("<del", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("old line", result.Html);
        Assert.Contains("<ins", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new line", result.Html);
    }
}
