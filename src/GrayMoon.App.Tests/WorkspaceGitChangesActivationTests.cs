using GrayMoon.Abstractions.Agent;
using GrayMoon.App.Services.Agent;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Ui;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceGitChangesActivationTests
{
    [Fact]
    public async Task Activate_when_workspace_is_cold_starts_one_scan()
    {
        var (activation, tracker, scanner, jobs) = CreateActivation(agentConnected: true);

        using var lease = activation.Activate(8);

        Assert.True(tracker.IsActive(8));
        await WaitUntilAsync(() => scanner.Calls == 1);
        Assert.Equal(8, scanner.LastWorkspaceId);

        jobs.Dispose();
    }

    [Fact]
    public void Activate_when_already_active_does_not_start_scan()
    {
        var (activation, tracker, scanner, jobs) = CreateActivation(agentConnected: true);
        using var existing = tracker.Subscribe(8);

        using var lease = activation.Activate(8);

        Assert.Equal(0, scanner.Calls);
        Assert.False(jobs.IsRunning(WorkspaceJobKeys.GitChangesScanKey(8)));

        jobs.Dispose();
    }

    [Fact]
    public void Activate_when_agent_is_offline_subscribes_but_does_not_scan()
    {
        var (activation, tracker, scanner, jobs) = CreateActivation(agentConnected: false);

        using var lease = activation.Activate(8);

        Assert.True(tracker.IsActive(8));
        Assert.Equal(0, scanner.Calls);
        Assert.False(jobs.IsRunning(WorkspaceJobKeys.GitChangesScanKey(8)));

        jobs.Dispose();
    }

    [Fact]
    public async Task Activate_when_scan_already_running_does_not_start_another()
    {
        var (activation, _, scanner, jobs) = CreateActivation(agentConnected: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        jobs.StartJob(WorkspaceJobKeys.GitChangesScanKey(8), "Refreshing...", async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task;

        using var lease = activation.Activate(8);

        Assert.Equal(0, scanner.Calls);

        release.SetResult();
        jobs.Dispose();
    }

    [Fact]
    public async Task Activate_when_workspace_mutation_is_running_does_not_start_scan()
    {
        var (activation, tracker, scanner, jobs) = CreateActivation(agentConnected: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        jobs.StartJob(WorkspaceJobKeys.RepositoriesOverlayKey(8), "Pushing...", async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task;

        using var lease = activation.Activate(8);

        Assert.True(tracker.IsActive(8));
        Assert.Equal(0, scanner.Calls);

        release.SetResult();
        jobs.Dispose();
    }

    private static (
        WorkspaceGitChangesActivation Activation,
        WorkspaceGitChangesActivityTracker Tracker,
        RecordingScanner Scanner,
        BackgroundJobService Jobs) CreateActivation(bool agentConnected)
    {
        var tracker = new WorkspaceGitChangesActivityTracker(
            Options.Create(new GitChangesOptions { WorkspaceActivityGraceMinutes = 8 }));
        var scanner = new RecordingScanner();
        var runner = new WorkspaceOperationRunner(NullLogger<WorkspaceOperationRunner>.Instance);
        var jobs = new BackgroundJobService(runner, NullLogger<BackgroundJobService>.Instance);
        var activation = new WorkspaceGitChangesActivation(
            tracker,
            scanner,
            new FakeAgentBridge(agentConnected),
            jobs,
            new NoopToastService(),
            NullLogger<WorkspaceGitChangesActivation>.Instance);
        return (activation, tracker, scanner, jobs);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for Git Changes activation.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class RecordingScanner : IGitChangesWorkspaceScanner
    {
        public int Calls;
        public int? LastWorkspaceId;

        public Task ScanWorkspaceAsync(
            int workspaceId,
            CancellationToken cancellationToken,
            Action<GitChangesWorkspaceScanProgress>? onProgress = null)
        {
            LastWorkspaceId = workspaceId;
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAgentBridge(bool connected) : IAgentBridge
    {
        public bool IsAgentConnected { get; } = connected;

        public Task<AgentCommandResponse> SendCommandAsync(
            string command,
            object args,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NoopToastService : IToastService
    {
#pragma warning disable CS0067
        public event Action? OnShow;
#pragma warning restore CS0067
        public string? Message => null;
        public bool IsVisible => false;
        public bool IsError => false;
        public void Show(string message) { }
        public void ShowError(string message) { }
        public void Hide() { }
    }
}
