using GrayMoon.App.Services.Worker;

namespace GrayMoon.App.Tests;

public sealed class WorkerUpgradeStartupPromptTests
{
    [Fact]
    public void Startup_offline_does_not_prompt()
    {
        var prompt = new WorkerUpgradeStartupPrompt();
        prompt.OnDesktopConnected();

        var observation = prompt.Observe(WorkerConnectionState.Offline, selfUpdateInProgress: false, workerSemVer: null, appSemVer: "1.0.0");

        Assert.False(observation.SuppressUpdateNotification);
        Assert.Null(observation.Prompt);
    }

    [Fact]
    public void First_mismatch_with_desktop_already_connected_emits_once()
    {
        var prompt = new WorkerUpgradeStartupPrompt();
        prompt.OnDesktopConnected();

        var observation = prompt.Observe(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: false, "0.9.0", "1.0.0");

        Assert.True(observation.SuppressUpdateNotification);
        Assert.Equal("0.9.0", observation.Prompt!.WorkerVersion);
        Assert.Equal("1.0.0", observation.Prompt.AppVersion);
        Assert.Null(prompt.OnDesktopConnected());
    }

    [Fact]
    public void Mismatch_before_desktop_connects_is_delivered_on_connect_only()
    {
        var prompt = new WorkerUpgradeStartupPrompt();

        var observation = prompt.Observe(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: false, "0.9.0", "1.0.0");

        Assert.True(observation.SuppressUpdateNotification);
        Assert.Null(observation.Prompt);

        var caughtUp = prompt.OnDesktopConnected();
        Assert.Equal("0.9.0", caughtUp!.WorkerVersion);

        prompt.OnDesktopDisconnected();
        Assert.Null(prompt.OnDesktopConnected());
    }

    [Fact]
    public void Later_mismatch_after_the_startup_offer_is_not_a_dialog()
    {
        var prompt = new WorkerUpgradeStartupPrompt();
        prompt.OnDesktopConnected();
        prompt.Observe(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: false, "0.9.0", "1.0.0");
        prompt.Observe(WorkerConnectionState.Offline, selfUpdateInProgress: false, null, "1.0.0");

        var again = prompt.Observe(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: false, "0.9.0", "1.0.0");

        Assert.False(again.SuppressUpdateNotification);
        Assert.Null(again.Prompt);
    }

    [Fact]
    public void Matching_worker_at_startup_suppresses_a_later_dialog()
    {
        var prompt = new WorkerUpgradeStartupPrompt();
        prompt.OnDesktopConnected();
        prompt.Observe(WorkerConnectionState.Online, selfUpdateInProgress: false, "1.0.0", "1.0.0");

        var later = prompt.Observe(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: false, "0.9.0", "1.0.0");

        Assert.False(later.SuppressUpdateNotification);
        Assert.Null(later.Prompt);
    }

    [Fact]
    public void Matching_worker_before_desktop_connects_cancels_the_pending_prompt()
    {
        var prompt = new WorkerUpgradeStartupPrompt();
        prompt.Observe(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: false, "0.9.0", "1.0.0");
        prompt.Observe(WorkerConnectionState.Online, selfUpdateInProgress: false, "1.0.0", "1.0.0");

        Assert.Null(prompt.OnDesktopConnected());
    }

    [Fact]
    public void Mismatch_during_self_update_is_not_the_startup_prompt()
    {
        var prompt = new WorkerUpgradeStartupPrompt();
        prompt.OnDesktopConnected();

        var duringUpdate = prompt.Observe(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: true, "0.9.0", "1.0.0");
        Assert.False(duringUpdate.SuppressUpdateNotification);

        var startup = prompt.Observe(WorkerConnectionState.VersionMismatch, selfUpdateInProgress: false, "0.9.0", "1.0.0");
        Assert.NotNull(startup.Prompt);
    }
}
