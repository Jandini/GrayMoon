using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Models;
using GrayMoon.App.Services.GitChanges;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>
/// <see cref="MarkdownImageEmbedder.EmbedAsync"/> loads embedded images with a bounded fan-out
/// (<see cref="WorkspaceOptions.MaxParallelOperations"/>) while keeping the sequential result: src values are
/// deduped, at most 24 images embed (the first 24 that load, in document order), and a failing image affects no other.
/// </summary>
public sealed class MarkdownImageEmbedderFanOutTests
{
    private const int MaxImages = 24;
    private static readonly string PngBase64 = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x01, 0x02]);

    [Fact]
    public async Task Images_load_concurrently_up_to_the_limit_and_all_are_embedded()
    {
        var (embedder, probe) = Create(maxParallel: 3);

        var result = await EmbedAsync(embedder, Html(10));

        Assert.Equal(10, probe.Calls);
        Assert.Equal(3, probe.MaxActive);
        for (var i = 0; i < 10; i++)
        {
            Assert.Contains($"src=\"data:image/png;base64,{PngBase64}\"", result);
            Assert.DoesNotContain($"src=\"img{i}.png\"", result);
        }
    }

    [Fact]
    public async Task Limit_of_one_loads_sequentially()
    {
        var (embedder, probe) = Create(maxParallel: 1);

        await EmbedAsync(embedder, Html(5));

        Assert.Equal(5, probe.Calls);
        Assert.Equal(1, probe.MaxActive);
    }

    [Fact]
    public async Task Duplicate_sources_are_loaded_once()
    {
        var (embedder, probe) = Create(maxParallel: 4);
        const string html = """<img src="a.png"><img src="b.png"><img src="a.png"><img src='b.png'><img src="data:image/png;base64,AAAA">""";

        var result = await EmbedAsync(embedder, html);

        Assert.Equal(2, probe.Calls);
        Assert.DoesNotContain("src=\"a.png\"", result);
        Assert.DoesNotContain("src='b.png'", result);
        Assert.Contains("src=\"data:image/png;base64,AAAA\"", result);
    }

    [Fact]
    public async Task Embedding_is_capped_at_max_images_in_document_order()
    {
        var (embedder, probe) = Create(maxParallel: 8);

        var result = await EmbedAsync(embedder, Html(MaxImages + 6));

        Assert.Equal(MaxImages, probe.Calls);
        Assert.True(probe.MaxActive <= 8);
        for (var i = 0; i < MaxImages; i++)
        {
            Assert.DoesNotContain($"src=\"img{i}.png\"", result);
        }

        for (var i = MaxImages; i < MaxImages + 6; i++)
        {
            Assert.Contains($"src=\"img{i}.png\"", result);
        }
    }

    [Fact]
    public async Task A_failing_image_does_not_affect_the_others_and_does_not_use_up_the_cap()
    {
        var (embedder, probe) = Create(maxParallel: 4, failing: ["img2.png", "img4.png"]);

        var result = await EmbedAsync(embedder, Html(MaxImages + 4));

        // The two failures are skipped; the first MaxImages loadable images (img0..img25 without 2 and 4) embed.
        Assert.Contains("src=\"img2.png\"", result);
        Assert.Contains("src=\"img4.png\"", result);
        var embeddedCount = result.Split("data:image/png;base64,").Length - 1;
        Assert.Equal(MaxImages, embeddedCount);
        Assert.DoesNotContain("src=\"img25.png\"", result);
        Assert.Contains("src=\"img26.png\"", result);
        Assert.DoesNotContain("src=\"img3.png\"", result);
        Assert.True(probe.Calls >= MaxImages);
    }

    [Fact]
    public async Task A_throwing_worker_call_is_swallowed_per_image()
    {
        var (embedder, _) = Create(maxParallel: 4, throwing: ["img1.png"]);

        var result = await EmbedAsync(embedder, Html(4));

        Assert.Contains("src=\"img1.png\"", result);
        Assert.DoesNotContain("src=\"img0.png\"", result);
        Assert.DoesNotContain("src=\"img2.png\"", result);
        Assert.DoesNotContain("src=\"img3.png\"", result);
    }

    private static string Html(int count) =>
        "<p>" + string.Concat(Enumerable.Range(0, count).Select(i => $"<img src=\"img{i}.png\" alt=\"i{i}\">")) + "</p>";

    private static Task<string> EmbedAsync(MarkdownImageEmbedder embedder, string html) =>
        embedder.EmbedAsync(html, @"C:\ws", "ws", "repo", null, "README.md");

    private static (MarkdownImageEmbedder Embedder, ImageEmbedFanOutProbe Probe) Create(
        int maxParallel, string[]? failing = null, string[]? throwing = null)
    {
        var probe = new ImageEmbedFanOutProbe();
        var bridge = new FakeWorkerBridge();
        bridge.RespondAsync("GetFileContents", async (args, ct) =>
        {
            var path = (string)args.GetType().GetProperty("filePath")!.GetValue(args)!;
            await probe.RunAsync(TimeSpan.FromMilliseconds(40), ct);
            if (throwing?.Contains(path) == true)
            {
                throw new InvalidOperationException("worker exploded");
            }

            return failing?.Contains(path) == true
                ? new WorkerCommandResponse(false, null, "not found")
                : new WorkerCommandResponse(true, new { contentBase64 = PngBase64, contentType = "image/png" }, null);
        });

        var embedder = new MarkdownImageEmbedder(
            bridge,
            new NoHttpClientFactory(),
            NullLogger<MarkdownImageEmbedder>.Instance,
            Options.Create(new WorkspaceOptions { MaxParallelOperations = maxParallel }));
        return (embedder, probe);
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No HTTP expected.");
    }

    /// <summary>Counts calls and tracks how many overlap.</summary>
    private sealed class ImageEmbedFanOutProbe
    {
        private readonly object _gate = new();
        private int _active;

        public int Calls { get; private set; }
        public int MaxActive { get; private set; }

        public async Task RunAsync(TimeSpan duration, CancellationToken ct)
        {
            lock (_gate)
            {
                Calls++;
                _active++;
                MaxActive = Math.Max(MaxActive, _active);
            }

            try
            {
                await Task.Delay(duration, ct);
            }
            finally
            {
                lock (_gate)
                {
                    _active--;
                }
            }
        }
    }
}
