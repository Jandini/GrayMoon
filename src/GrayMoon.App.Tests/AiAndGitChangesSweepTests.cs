using System.Net;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Ai;
using GrayMoon.App.Services.GitChanges;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>
/// Regression tests for the small AI and Git Changes fixes of the 2026-11-10 review (P3 bullets): the image embedder
/// requests the decoded URL, and a Cancel that races the end of an AI run never reaches a disposed source.
/// </summary>
public sealed class AiAndGitChangesSweepTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x01, 0x02];

    [Fact]
    public async Task Remote_image_is_requested_with_the_decoded_url_and_replaced_in_the_escaped_html()
    {
        var http = new RecordingHttpHandler();
        var embedder = CreateEmbedder(new FakeWorkerBridge(), http);
        const string html = """<p><img src="https://img.example/badge.png?branch=main&amp;event=push" alt="Build"></p>""";

        var result = await embedder.EmbedAsync(html, @"C:\ws", "ws", "repo", null, "README.md");

        Assert.Equal("https://img.example/badge.png?branch=main&event=push", Assert.Single(http.Requested));
        Assert.Contains($"src=\"data:image/png;base64,{Convert.ToBase64String(Png)}\"", result);
        Assert.DoesNotContain("img.example", result);
    }

    [Fact]
    public async Task Repository_image_path_is_decoded_before_it_is_sent_to_the_worker()
    {
        var bridge = new FakeWorkerBridge();
        var paths = new List<string>();
        bridge.RespondAsync("GetFileContents", (args, _) =>
        {
            lock (paths)
                paths.Add((string)args.GetType().GetProperty("filePath")!.GetValue(args)!);
            return Task.FromResult(new WorkerCommandResponse(true, new { contentBase64 = Convert.ToBase64String(Png), contentType = "image/png" }, null));
        });
        var embedder = CreateEmbedder(bridge, new RecordingHttpHandler());
        const string html = """<p><img src="docs/R&amp;D.png" alt="x"></p>""";

        var result = await embedder.EmbedAsync(html, @"C:\ws", "ws", "repo", null, "README.md");

        Assert.Equal("docs/R&D.png", Assert.Single(paths));
        Assert.Contains("src=\"data:image/png;base64,", result);
    }

    [Fact]
    public void Fallback_chip_title_is_escaped_once()
    {
        const string html = """<img src="https://img.example/a.png?x=1&amp;y=2" alt="A">""";

        var result = MarkdownImageEmbedder.RewriteUnembeddedRemoteImages(html);

        Assert.Contains("title=\"https://img.example/a.png?x=1&amp;y=2\"", result);
        Assert.DoesNotContain("&amp;amp;", result);
    }

    [Fact]
    public async Task Cancel_racing_the_end_of_a_run_never_throws()
    {
        var runs = new TestRuns();
        var failures = new List<Exception>();

        for (var i = 0; i < 4000 && failures.Count == 0; i++)
        {
            var key = new AiTextGenerationRuns<string>.Key(1, i);
            // No RunContinuationsAsynchronously: the run finishes (and disposes its source) inline on the completing thread.
            var completion = new TaskCompletionSource<AiTextGenerationRuns<string>.Outcome>();
            Assert.True(runs.TryStart(key, _ => completion.Task, consult: null));

            using var barrier = new Barrier(2);
            var finish = Task.Run(() =>
            {
                barrier.SignalAndWait();
                completion.SetResult(AiTextGenerationRuns<string>.Outcome.Succeeded("text", "Claude", ""));
            });
            var cancel = Task.Run(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    runs.Cancel(key);
                }
                catch (Exception ex)
                {
                    lock (failures)
                        failures.Add(ex);
                }
            });
            await Task.WhenAll(finish, cancel);
            Assert.False(runs.IsRunning(key));
        }

        Assert.Empty(failures);
    }

    [Fact]
    public async Task Cancel_still_cancels_a_run_that_is_in_flight()
    {
        var runs = new TestRuns();
        var key = new AiTextGenerationRuns<string>.Key(1, 1);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Changed += k =>
        {
            if (!runs.IsRunning(k))
                ended.TrySetResult();
        };

        Assert.True(runs.TryStart(key, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return AiTextGenerationRuns<string>.Outcome.Succeeded("text", "Claude", "");
        }, consult: null));
        runs.Cancel(key);
        runs.Cancel(key);

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(runs.IsRunning(key));
        Assert.Null(runs.Error(key));
        Assert.Empty(runs.Stored);
    }

    private static MarkdownImageEmbedder CreateEmbedder(FakeWorkerBridge bridge, RecordingHttpHandler http) =>
        new(
            bridge,
            new SingleHandlerHttpClientFactory(http),
            NullLogger<MarkdownImageEmbedder>.Instance,
            Options.Create(new WorkspaceOptions { MaxParallelOperations = 2 }));

    private sealed class TestRuns() : AiTextGenerationRuns<string>(NullLogger.Instance, "failed")
    {
        public List<string> Stored { get; } = [];

        protected override string CurrentText(Key key) => "";

        protected override void Store(Key key, string text)
        {
            lock (Stored)
                Stored.Add(text);
        }
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requested)
                Requested.Add(request.RequestUri!.AbsoluteUri);
            // Only the exact decoded URL answers, like a server that needs every query parameter.
            var ok = request.RequestUri!.Query == "?branch=main&event=push";
            return Task.FromResult(ok
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class SingleHandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
