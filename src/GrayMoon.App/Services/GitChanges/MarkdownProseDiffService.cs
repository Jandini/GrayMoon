using System.Text;
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

    // Keep entire fenced code blocks atomic (including rewritten <pre class="mermaid">) so tree
    // diagrams and Mermaid source are not word-diffed. HtmlDiff allows only non-overlapping
    // block expressions - do not register a second mermaid-only pattern.
    private static readonly Regex AnyPreBlock = new(
        @"<pre\b[\s\S]*?</pre>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Markdig GFM emits either pre>code.language-mermaid or (with some renderers) pre.language-mermaid.
    private static readonly Regex MermaidCodeFence = new(
        @"<pre><code\s+class=""language-mermaid"">([\s\S]*?)</code></pre>|<pre\s+class=""language-mermaid"">([\s\S]*?)</pre>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public MarkdownProseDiffResult Render(string? originalMarkdown, string? modifiedMarkdown, MarkdownProsePreviewMode mode)
    {
        var original = RepairUtf8Mojibake(originalMarkdown ?? string.Empty);
        var modified = RepairUtf8Mojibake(modifiedMarkdown ?? string.Empty);

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
        // One block expression only - HtmlDiff rejects overlapping patterns. Mermaid fences are
        // already rewritten to <pre class="mermaid">, so AnyPreBlock covers those too.
        differ.AddBlockExpression(AnyPreBlock);
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

    /// <summary>
    /// Repairs text that was UTF-8 box-drawing / arrows mis-decoded as Windows-1252 and then
    /// saved again as UTF-8 (classic <c>â"œâ"€â"€</c> for <c>├──</c>). Prefer targeted replacements
    /// so clean Unicode elsewhere is left alone.
    /// </summary>
    internal static string RepairUtf8Mojibake(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\u00E2') < 0)
        {
            return text;
        }

        // UTF-8 for ├ (E2 94 9C) / ─ (E2 94 80) / └ (E2 94 94) / → (E2 86 92) / ← (E2 86 90)
        // mis-read as CP1252 yields these Unicode sequences when re-saved as UTF-8.
        var repaired = text
            .Replace("\u00E2\u201D\u0153", "\u251C", StringComparison.Ordinal) // â"œ -> ├  (0x94 as U+201D)
            .Replace("\u00E2\u201C\u0153", "\u251C", StringComparison.Ordinal) // â"œ variant (0x93 as U+201C)
            .Replace("\u00E2\u201D\u20AC", "\u2500", StringComparison.Ordinal) // â"€ -> ─
            .Replace("\u00E2\u201C\u20AC", "\u2500", StringComparison.Ordinal)
            .Replace("\u00E2\u201D\u201D", "\u2514", StringComparison.Ordinal) // â"" -> └ (0x94 0x94)
            .Replace("\u00E2\u201C\u201D", "\u2514", StringComparison.Ordinal)
            .Replace("\u00E2\u20AC\u2122", "\u2192", StringComparison.Ordinal) // â€™ messy - try common → forms
            .Replace("\u00E2\u2020\u2019", "\u2192", StringComparison.Ordinal) // â†' as seen in some files
            .Replace("\u00E2\u2020\u2018", "\u2190", StringComparison.Ordinal);

        // Fallback: whole-string CP1252 round-trip when targeted replaces did nothing useful
        // but classic box-drawing mojibake markers remain.
        if (repaired.IndexOf('\u00E2') >= 0
            && (repaired.Contains("\u00E2\u201D", StringComparison.Ordinal) || repaired.Contains("\u00E2\u201C", StringComparison.Ordinal)))
        {
            try
            {
                var latin1 = Encoding.GetEncoding(1252);
                var roundTrip = Encoding.UTF8.GetString(latin1.GetBytes(repaired));
                if (roundTrip.Contains('\u251C') || roundTrip.Contains('\u2514') || roundTrip.Contains('\u2500'))
                {
                    return roundTrip;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return repaired;
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
        // data: URIs after MarkdownImageEmbedder rewrites relative/remote images.
        sanitizer.AllowedSchemes.Add("data");
        return sanitizer;
    }
}
