using GrayMoon.App.Services.GitChanges;

namespace GrayMoon.App.Tests;

public sealed class MarkdownImageEmbedderTests
{
    [Fact]
    public void RewriteUnembeddedRemoteImages_replaces_badge_with_compact_alt_chip()
    {
        const string html =
            """<p><a href="https://github.com/org/repo/actions"><img src="https://github.com/org/repo/actions/workflows/build.yml/badge.svg" alt="Build"></a></p>""";

        var result = MarkdownImageEmbedder.RewriteUnembeddedRemoteImages(html);

        Assert.DoesNotContain("<img", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("markdown-img-fallback", result);
        Assert.Contains(">Build</span>", result);
        Assert.Contains("href=\"https://github.com/org/repo/actions\"", result);
    }

    [Fact]
    public void RewriteUnembeddedRemoteImages_leaves_data_uri_images_alone()
    {
        const string html = """<p><img src="data:image/png;base64,abc" alt="ok"></p>""";

        var result = MarkdownImageEmbedder.RewriteUnembeddedRemoteImages(html);

        Assert.Contains("data:image/png;base64,abc", result);
        Assert.DoesNotContain("markdown-img-fallback", result);
    }
}
