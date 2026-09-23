using System.Text.RegularExpressions;
using Ganss.Xss;
using Markdig;

namespace GrayMoon.App.Services.GitChanges;

public enum MarkdownProsePreviewMode
{
    Changes,
    After,
    Before,
}

public sealed class MarkdownProseDiffResult
{
    public required bool Success { get; init; }
    public string? Html { get; init; }
    public string? ErrorMessage { get; init; }
    public bool TooLarge { get; init; }
}

/// <summary>
/// Renders markdown (GFM-ish) to sanitized HTML for Git Changes Preview, including HtmlDiff for Changes mode.
/// Mermaid fences are rewritten to <c>pre.mermaid</c> so client JS can render them; they are treated as atomic
/// blocks during HtmlDiff so diagram source is never word-diffed inside the fence.
/// </summary>
public sealed class MarkdownProseDiffService
{
    /// <summary>Soft cap per side (~512 KiB of UTF-16 chars) - HtmlDiff is heavier than Monaco paint.</summary>
    public const int SoftCharLimitPerSide = 512 * 1024;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    private static readonly Regex MermaidPreBlock = new(
        @"<pre\s+class=""mermaid"">[\s\S]*?</pre>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Markdig GFM emits either pre>code.language-mermaid or (with some renderers) pre.language-mermaid.
    private static readonly Regex MermaidCodeFence = new(
        @"<pre><code\s+class=""language-mermaid"">([\s\S]*?)</code></pre>|<pre\s+class=""language-mermaid"">([\s\S]*?)</pre>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public MarkdownProseDiffResult Render(string? originalMarkdown, string? modifiedMarkdown, MarkdownProsePreviewMode mode)
    {
        var original = originalMarkdown ?? string.Empty;
        var modified = modifiedMarkdown ?? string.Empty;

        if (original.Length > SoftCharLimitPerSide || modified.Length > SoftCharLimitPerSide)
        {
            return new MarkdownProseDiffResult
            {
                Success = false,
                TooLarge = true,
                ErrorMessage = "This markdown file is too large for rendered preview. Switch to Source.",
            };
        }

        try
        {
            var html = mode switch
            {
                MarkdownProsePreviewMode.After => Sanitize(ToHtml(modified)),
                MarkdownProsePreviewMode.Before => Sanitize(ToHtml(original)),
                _ => DiffAndSanitize(original, modified),
            };

            if (string.IsNullOrWhiteSpace(html))
            {
                return new MarkdownProseDiffResult
                {
                    Success = true,
                    Html = string.Empty,
                };
            }

            return new MarkdownProseDiffResult
            {
                Success = true,
                Html = html,
            };
        }
        catch (Exception ex)
        {
            return new MarkdownProseDiffResult
            {
                Success = false,
                ErrorMessage = $"Failed to render markdown preview: {ex.Message}",
            };
        }
    }

    private static string DiffAndSanitize(string originalMarkdown, string modifiedMarkdown)
    {
        if (string.IsNullOrEmpty(originalMarkdown) && !string.IsNullOrEmpty(modifiedMarkdown))
        {
            return Sanitize($"<div class=\"markdown-diff-all-ins\">{ToHtml(modifiedMarkdown)}</div>");
        }

        if (!string.IsNullOrEmpty(originalMarkdown) && string.IsNullOrEmpty(modifiedMarkdown))
        {
            return Sanitize($"<div class=\"markdown-diff-all-del\">{ToHtml(originalMarkdown)}</div>");
        }

        if (string.IsNullOrEmpty(originalMarkdown) && string.IsNullOrEmpty(modifiedMarkdown))
        {
            return string.Empty;
        }

        var oldHtml = ToHtml(originalMarkdown);
        var newHtml = ToHtml(modifiedMarkdown);
        var differ = new HtmlDiff.HtmlDiff(oldHtml, newHtml);
        differ.AddBlockExpression(MermaidPreBlock);
        var diffHtml = differ.Build();
        return Sanitize(diffHtml);
    }

    private static string ToHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var html = Markdown.ToHtml(markdown, Pipeline);
        return RewriteMermaidFences(html);
    }

    private static string RewriteMermaidFences(string html)
    {
        return MermaidCodeFence.Replace(html, static match =>
        {
            var body = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return $"<pre class=\"mermaid\">{body}</pre>";
        });
    }

    private static string Sanitize(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        return Sanitizer.Sanitize(html);
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Add("ins");
        sanitizer.AllowedTags.Add("del");
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedAttributes.Add("id");
        sanitizer.AllowedAttributes.Add("data-operation-index");
        sanitizer.AllowedAttributes.Add("href");
        sanitizer.AllowedAttributes.Add("title");
        sanitizer.AllowedAttributes.Add("target");
        sanitizer.AllowedAttributes.Add("rel");
        sanitizer.AllowedAttributes.Add("colspan");
        sanitizer.AllowedAttributes.Add("rowspan");
        sanitizer.AllowedAttributes.Add("align");
        sanitizer.AllowedAttributes.Add("start");
        sanitizer.AllowedAttributes.Add("checked");
        sanitizer.AllowedAttributes.Add("type");
        sanitizer.AllowedAttributes.Add("disabled");
        return sanitizer;
    }
}
