using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Hosted;
using GrayMoon.Worker.Hub;
using GrayMoon.Worker.Jobs;
using GrayMoon.Worker.Queue;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The SignalR client runs one handler at a time. A RequestCommand handler that waits for room in a full queue
/// therefore holds back every later message, including the CancelCommand meant to end that wait. These tests fill a
/// real bounded queue with nothing reading it, which is what a busy pool looks like to the handler.
/// </summary>
public sealed class CommandRequestIntakeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly CommandJobCancellationRegistry _registry = new();
    private readonly ReadJobQueue _queue = new(Options.Create(new WorkerOptions { MaxConcurrentReadCommands = 1 }));
    private readonly CommandJobFactory _factory = new();
    private readonly List<(string RequestId, WorkerCommandResponse Response)> _responses = [];
    private readonly SemaphoreSlim _responded = new(0);
    private readonly CommandRequestIntake _intake;

    public CommandRequestIntakeTests()
    {
        _intake = new CommandRequestIntake(_registry, NullLogger.Instance);
    }

    [Fact]
    public async Task A_full_queue_does_not_hold_the_handler_and_a_cancel_ends_the_wait()
    {
        var capacity = await FillQueueAsync();

        var accept = Accept("blocked");

        Assert.True(accept.IsCompleted, "the handler must return while the queue is full, or CancelCommand cannot be delivered");
        Assert.Empty(Responses());

        _registry.Cancel("blocked");

        Assert.True(await _responded.WaitAsync(Wait), "the cancelled request never got a response");
        var (requestId, response) = Assert.Single(Responses());
        Assert.Equal("blocked", requestId);
        Assert.False(response.Success);
        Assert.Equal("Command cancelled.", response.Error);
        Assert.False(_registry.IsCancelled("blocked"));
        Assert.Equal(capacity, _registry.Count);

        var queued = await DrainAsync(capacity);
        Assert.DoesNotContain("blocked", queued);
    }

    [Fact]
    public async Task Requests_that_waited_for_room_are_queued_in_arrival_order()
    {
        var capacity = await FillQueueAsync();
        await Accept("first");
        await Accept("second");
        await Accept("third");

        var queued = await DrainAsync(capacity + 3);

        Assert.Equal(["first", "second", "third"], queued.Skip(capacity));
        Assert.Empty(Responses());
    }

    [Fact]
    public async Task A_request_cancelled_before_it_arrived_is_answered_and_never_queued()
    {
        _registry.Cancel("early");

        await Accept("early");

        Assert.True(await _responded.WaitAsync(Wait));
        Assert.Equal("Command cancelled.", Assert.Single(Responses()).Response.Error);
        Assert.Equal(0, _registry.Count);
    }

    [Fact]
    public async Task Stopping_the_worker_ends_a_wait_for_room()
    {
        await FillQueueAsync();
        using var host = new CancellationTokenSource();

        await Accept("blocked", host.Token);
        await host.CancelAsync();

        Assert.True(await _responded.WaitAsync(Wait), "the wait for room outlived the Worker");
        Assert.Equal("blocked", Assert.Single(Responses()).RequestId);
    }

    private Task Accept(string requestId, CancellationToken hostToken = default)
        => _intake.AcceptAsync(
            requestId,
            WorkerHubMethods.GetHostInfo,
            () => _factory.CreateCommandJob(requestId, WorkerHubMethods.GetHostInfo, JsonDocument.Parse("{}").RootElement),
            _queue,
            hostToken,
            (id, response) =>
            {
                lock (_responses)
                    _responses.Add((id, response));
                _responded.Release();
                return Task.CompletedTask;
            });

    private List<(string RequestId, WorkerCommandResponse Response)> Responses()
    {
        lock (_responses)
            return [.. _responses];
    }

    /// <summary>Fills the queue to its bound through the intake and returns that bound.</summary>
    private async Task<int> FillQueueAsync()
    {
        const int capacity = 8; // ReadJobQueue: max(MaxConcurrentReadCommands * 2, 8)
        for (var i = 0; i < capacity; i++)
            await Accept($"filler-{i}");

        // Proves the bound: one more write does not complete.
        using var probe = new CancellationTokenSource();
        var extra = _queue.EnqueueAsync(_factory.CreateCommandJob("probe", WorkerHubMethods.GetHostInfo, JsonDocument.Parse("{}").RootElement), probe.Token);
        Assert.False(extra.IsCompleted, "the queue was expected to be full");
        await probe.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await extra);
        return capacity;
    }

    private async Task<List<string>> DrainAsync(int count)
    {
        var ids = new List<string>();
        using var timeout = new CancellationTokenSource(Wait);
        await foreach (var envelope in _queue.ReadAllAsync(timeout.Token))
        {
            ids.Add(envelope.CommandJob!.RequestId);
            if (ids.Count == count)
                break;
        }

        return ids;
    }
}

/// <summary>Remembered cancellations for requests that never arrive must not pile up for the life of the Worker.</summary>
public sealed class CommandJobCancellationRegistryTests
{
    [Fact]
    public void Cancellations_for_unknown_requests_are_bounded()
    {
        var registry = new CommandJobCancellationRegistry();

        for (var i = 0; i < CommandJobCancellationRegistry.MaxPendingCancellations * 4; i++)
            registry.Cancel($"finished-{i}");

        Assert.Equal(CommandJobCancellationRegistry.MaxPendingCancellations, registry.Count);
    }

    [Fact]
    public void A_recent_early_cancel_still_reaches_its_request()
    {
        var registry = new CommandJobCancellationRegistry();
        for (var i = 0; i < CommandJobCancellationRegistry.MaxPendingCancellations * 2; i++)
            registry.Cancel($"finished-{i}");

        registry.Cancel("early");
        var cts = registry.Register("early", CancellationToken.None);

        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void Trimming_never_drops_a_registered_request()
    {
        var registry = new CommandJobCancellationRegistry();
        var running = registry.Register("running", CancellationToken.None);
        registry.Cancel("early");
        registry.Register("early", CancellationToken.None);
        registry.Unregister("early");
        var reused = registry.Register("early", CancellationToken.None);

        for (var i = 0; i < CommandJobCancellationRegistry.MaxPendingCancellations * 2; i++)
            registry.Cancel($"finished-{i}");

        Assert.False(running.IsCancellationRequested);
        Assert.False(reused.IsCancellationRequested);
        Assert.Same(running, registry.Register("running", CancellationToken.None));
        Assert.Same(reused, registry.Register("early", CancellationToken.None));
        registry.Cancel("running");
        Assert.True(running.IsCancellationRequested);
    }
}

/// <summary>A connection that closes while the Worker stops must not start reconnecting, and a running reconnect loop must end.</summary>
public sealed class SignalRReconnectShutdownTests
{
    [Fact]
    public async Task A_close_after_stop_starts_no_reconnect_loop()
    {
        await using var service = NewService();
        // The startup token is never cancelled, as in the real host once StartAsync has returned.
        service.BuildConnection(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);
        await service.HandleClosedAsync(null);

        Assert.Null(service.ReconnectTask);
    }

    [Fact]
    public async Task Stop_ends_a_running_reconnect_loop()
    {
        await using var service = NewService();
        service.BuildConnection(CancellationToken.None);

        await service.HandleClosedAsync(null);
        var loop = service.ReconnectTask;
        Assert.NotNull(loop);

        await service.StopAsync(CancellationToken.None);

        await loop!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Start_returns_while_the_App_is_down_and_stop_ends_the_connect_loop()
    {
        // Nothing listens on the hub URL. The host starts hosted services one after another, so a start that
        // waits for the App would keep the hook listener and the job pools from starting.
        await using var service = NewService();

        await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var connect = service.ConnectTask;
        Assert.NotNull(connect);
        Assert.False(connect!.IsCompleted);

        await service.StopAsync(CancellationToken.None);

        await connect.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static SignalRConnectionHostedService NewService()
    {
        var options = Options.Create(new WorkerOptions { AppHubUrl = $"http://127.0.0.1:{FakeAppApi.FindClosedPort()}/hub/worker" });
        var queue = new StubQueue();
        return new SignalRConnectionHostedService(
            new HubConnectionProvider(),
            queue,
            queue,
            queue,
            queue,
            new CommandJobFactory(),
            new CommandJobCancellationRegistry(),
            new FakeWorkerSecretProvider("secret"),
            options,
            NullLogger<SignalRConnectionHostedService>.Instance);
    }

    private sealed class StubQueue : IJobQueue, IReadJobQueue, IDiffJobQueue, IAiJobQueue
    {
        public ValueTask EnqueueAsync(JobEnvelope job, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public IAsyncEnumerable<JobEnvelope> ReadAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
