using System.Reflection;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Services;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Ui;
using GrayMoon.App.Services.Worker;
using GrayMoon.Common.Git;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

/// <summary>Review 2026-11-10, finding 3: handlers on the singleton tracker are removed again.</summary>
public sealed class WorkerConnectionTrackerSubscriptionTests
{
    [Fact]
    public void Handlers_added_and_removed_by_one_hundred_components_leave_none_behind()
    {
        var tracker = new WorkerConnectionTracker("1.0.0");
        var components = Enumerable.Range(0, 100).Select(_ => new FakeComponent(tracker)).ToList();
        Assert.Equal(100, tracker.StateChangedHandlerCount);

        foreach (var component in components)
            component.Dispose();

        Assert.Equal(0, tracker.StateChangedHandlerCount);

        tracker.OnWorkerConnected("c1");
        Assert.All(components, c => Assert.Equal(1, c.Notifications));
    }

    [Fact]
    public void Badge_removes_its_tracker_handler_and_the_static_layout_does_not_subscribe()
    {
        var components = Path.Combine(SourceTree.AppProjectDirectory, "Components");
        var badge = File.ReadAllText(Path.Combine(components, "Shared", "WorkerStatusBadge.razor"));
        Assert.Contains("WorkerConnectionTracker.OnStateChanged(OnWorkerStateChanged)", badge);
        Assert.Contains("WorkerConnectionTracker.RemoveStateChanged(OnWorkerStateChanged)", badge);
        Assert.Contains("RendererInfo.IsInteractive", badge);

        // The layout is rendered statically on every request and never disposed by a circuit.
        var layout = File.ReadAllText(Path.Combine(components, "Layout", "MainLayout.razor"));
        Assert.DoesNotContain(".OnStateChanged(", layout);
    }

    [Fact]
    public void Activity_tracker_is_mounted_as_an_interactive_component()
    {
        // Mounted statically, its OnAfterRenderAsync never runs and the idle polling tier never applies.
        var layout = File.ReadAllText(Path.Combine(SourceTree.AppProjectDirectory, "Components", "Layout", "MainLayout.razor"));
        Assert.Contains("<AppActivityTracker @rendermode=", layout);
    }

    private sealed class FakeComponent : IDisposable
    {
        private readonly WorkerConnectionTracker _tracker;
        public int Notifications;

        public FakeComponent(WorkerConnectionTracker tracker)
        {
            _tracker = tracker;
            _tracker.OnStateChanged(OnChanged);
        }

        private void OnChanged(WorkerConnectionState state) => Notifications++;

        public void Dispose() => _tracker.RemoveStateChanged(OnChanged);
    }
}

/// <summary>Review 2026-11-10, finding 15: the toast service is per circuit, never held by a singleton.</summary>
public sealed class ToastServiceLifetimeTests
{
    [Fact]
    public void Toast_service_is_registered_scoped()
    {
        var program = File.ReadAllText(Path.Combine(SourceTree.AppProjectDirectory, "Program.cs"));
        Assert.Contains("AddScoped<IToastService, ToastService>()", program);
        Assert.DoesNotContain("AddSingleton<IToastService", program);
    }

    [Fact]
    public void No_singleton_or_hosted_service_injects_the_toast_service()
    {
        var program = File.ReadAllText(Path.Combine(SourceTree.AppProjectDirectory, "Program.cs"));
        var consumers = typeof(ToastService).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IToastService))))
            .ToList();

        Assert.NotEmpty(consumers);
        var offenders = consumers
            .Where(t => typeof(IHostedService).IsAssignableFrom(t)
                || program.Split('\n').Any(line =>
                    (line.Contains("AddSingleton", StringComparison.Ordinal) || line.Contains("AddHostedService", StringComparison.Ordinal))
                    && (line.Contains($"<{t.Name}>", StringComparison.Ordinal) || line.Contains($", {t.Name}>", StringComparison.Ordinal))))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Two_circuits_do_not_share_a_toast()
    {
        var services = new ServiceCollection();
        services.AddScoped<IToastService, ToastService>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var tabA = provider.CreateScope();
        using var tabB = provider.CreateScope();

        var toastA = tabA.ServiceProvider.GetRequiredService<IToastService>();
        var toastB = tabB.ServiceProvider.GetRequiredService<IToastService>();
        var shownInB = 0;
        toastB.OnShow += () => shownInB++;

        toastA.ShowError("Push failed.");

        Assert.True(toastA.IsVisible);
        Assert.False(toastB.IsVisible);
        Assert.Null(toastB.Message);
        Assert.Equal(0, shownInB);
    }

    [Fact]
    public void Development_container_validation_is_enabled()
    {
        var program = File.ReadAllText(Path.Combine(SourceTree.AppProjectDirectory, "Program.cs"));
        Assert.Contains("options.ValidateScopes = isDevelopment;", program);
        Assert.Contains("options.ValidateOnBuild = isDevelopment;", program);
    }
}

/// <summary>Review 2026-11-10, findings 9 and 32.</summary>
public sealed class WorkerResponseDeliveryPendingTests
{
    [Fact]
    public async Task Already_cancelled_token_leaves_nothing_pending()
    {
        var requestId = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = WorkerResponseDelivery.WaitAsync(requestId, cts.Token);

        Assert.False(WorkerResponseDelivery.IsPending(requestId));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Cancellation_after_the_wait_started_removes_the_entry()
    {
        var requestId = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();

        var task = WorkerResponseDelivery.WaitAsync(requestId, cts.Token);
        Assert.True(WorkerResponseDelivery.IsPending(requestId));

        cts.Cancel();

        Assert.False(WorkerResponseDelivery.IsPending(requestId));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Completed_request_is_delivered_and_removed()
    {
        var requestId = Guid.NewGuid().ToString("N");
        var task = WorkerResponseDelivery.WaitAsync(requestId, CancellationToken.None);

        WorkerResponseDelivery.Complete(requestId, new WorkerCommandResponse(true, "ok", null));

        Assert.True((await task).Success);
        Assert.False(WorkerResponseDelivery.IsPending(requestId));
    }

    [Fact]
    public void New_commands_are_routed_to_the_newest_connection_during_an_overlap()
    {
        var tracker = new WorkerConnectionTracker("1.0.0");
        tracker.OnWorkerConnected("old");
        tracker.OnWorkerConnected("new");

        Assert.Equal("new", tracker.GetWorkerConnectionId());

        tracker.OnWorkerDisconnected("old");
        Assert.Equal("new", tracker.GetWorkerConnectionId());
        Assert.True(tracker.HasWorkerConnection);

        tracker.OnWorkerDisconnected("new");
        Assert.Null(tracker.GetWorkerConnectionId());
        Assert.False(tracker.HasWorkerConnection);
    }

    [Fact]
    public async Task Disconnect_of_the_old_connection_fails_only_its_own_commands()
    {
        var onOld = Guid.NewGuid().ToString("N");
        var onNew = Guid.NewGuid().ToString("N");
        var oldConnection = "old-" + Guid.NewGuid().ToString("N");
        var newConnection = "new-" + Guid.NewGuid().ToString("N");
        var oldTask = WorkerResponseDelivery.WaitAsync(onOld, CancellationToken.None, connectionId: oldConnection);
        var newTask = WorkerResponseDelivery.WaitAsync(onNew, CancellationToken.None, connectionId: newConnection);

        WorkerResponseDelivery.FailForConnection(oldConnection, "Worker disconnected before responding.");

        var failed = await oldTask;
        Assert.False(failed.Success);
        Assert.Equal("Worker disconnected before responding.", failed.Error);
        Assert.False(newTask.IsCompleted);
        Assert.True(WorkerResponseDelivery.IsPending(onNew));

        WorkerResponseDelivery.Complete(onNew, new WorkerCommandResponse(true, null, null));
        Assert.True((await newTask).Success);
    }
}

/// <summary>Review 2026-11-10, finding 12: a refused start says so.</summary>
public sealed class BackgroundJobRefusedStartTests
{
    [Fact]
    public async Task Second_start_for_a_running_key_is_refused_and_its_work_is_not_run()
    {
        using var jobs = new BackgroundJobService(
            new WorkspaceOperationRunner(NullLogger<WorkspaceOperationRunner>.Instance),
            NullLogger<BackgroundJobService>.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRan = false;

        var first = jobs.StartJob("/connectors", "First", (_, _) => release.Task);
        var second = jobs.StartJob("/connectors", "Second", (_, _) =>
        {
            secondRan = true;
            return Task.CompletedTask;
        });

        Assert.True(first.WasStarted);
        Assert.False(second.WasStarted);
        Assert.Equal("First", second.DisplayMessage);
        Assert.Equal(BackgroundJobState.Running, second.State);

        // The refused caller cannot overwrite the running job's message.
        second.ReportProgress("Second is running");
        Assert.Equal("First", first.DisplayMessage);

        release.SetResult();
        await WaitForCompletedAsync(first);
        Assert.Equal(BackgroundJobState.Completed, second.State);
        Assert.False(secondRan);
    }

    [Fact]
    public async Task Workspace_job_refused_by_the_lock_reports_not_started()
    {
        var runner = new WorkspaceOperationRunner(NullLogger<WorkspaceOperationRunner>.Instance);
        // Two circuits (browser tabs) share the process-wide runner.
        using var tabA = new BackgroundJobService(runner, NullLogger<BackgroundJobService>.Instance);
        using var tabB = new BackgroundJobService(runner, NullLogger<BackgroundJobService>.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRan = false;
        var key = WorkspaceJobKeys.RepositoriesOverlayKey(4711);

        var first = tabA.StartJob(key, "Sync", (_, _) => release.Task);
        var second = tabB.StartJob(key, "Push", (_, _) =>
        {
            secondRan = true;
            return Task.CompletedTask;
        });

        Assert.True(first.WasStarted);
        Assert.False(second.WasStarted);
        Assert.Equal("Sync", second.DisplayMessage);

        release.SetResult();
        await WaitForCompletedAsync(first);
        Assert.False(secondRan);

        var third = tabB.StartJob(key, "Push", (_, _) => Task.CompletedTask);
        Assert.True(third.WasStarted);
    }

    private static async Task WaitForCompletedAsync(BackgroundJobHandle handle)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (handle.State == BackgroundJobState.Completed)
                done.TrySetResult();
        }

        handle.Changed += Check;
        Check();
        // The timeout only bounds a hang; the outcome does not depend on elapsed time.
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}

/// <summary>Review 2026-11-10, finding 30: only the stopping token ends the write queue reader.</summary>
public sealed class WorkspaceGitChangesWriteQueueReaderTests
{
    [Fact]
    public async Task Foreign_cancellation_in_one_snapshot_does_not_stop_the_reader()
    {
        var scopes = new ScriptedScopeFactory(
            new OperationCanceledException("A timeout inside the handler, not shutdown."),
            new InvalidOperationException("second snapshot reached the handler"));
        var queue = new WorkspaceGitChangesWriteQueue(scopes, NullLogger<WorkspaceGitChangesWriteQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            var first = queue.EnqueueAndWaitAsync(new GitChangesSnapshotNotification { Snapshot = null! });
            var second = queue.EnqueueAndWaitAsync(new GitChangesSnapshotNotification { Snapshot = null! });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(30)));

            // The reader is still alive: the next snapshot is taken off the queue and handled.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal("second snapshot reached the handler", ex.Message);
            Assert.Equal(2, scopes.Calls);
            Assert.False(queue.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }

    private sealed class ScriptedScopeFactory(params Exception[] failures) : IServiceScopeFactory
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public IServiceScope CreateScope()
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            throw failures[Math.Min(index, failures.Length - 1)];
        }
    }
}

internal static class SourceTree
{
    /// <summary>The GrayMoon.App project directory, found by walking up from the test output folder.</summary>
    public static string AppProjectDirectory
    {
        get
        {
            // The source path is compiled in, so the lookup also works with a redirected artifacts folder.
            var testProject = Path.GetDirectoryName(ThisFile())!;
            var app = Path.GetFullPath(Path.Combine(testProject, "..", "GrayMoon.App"));
            Assert.True(File.Exists(Path.Combine(app, "Program.cs")), $"GrayMoon.App not found at {app}");
            return app;
        }
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
