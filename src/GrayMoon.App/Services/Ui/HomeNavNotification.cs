using GrayMoon.App.Services.Worker;

namespace GrayMoon.App.Services.Ui;

/// <summary>
/// The Home nav circle. It is red only while the Worker is down, and it is gone as soon as the Worker is up.
/// A Worker update in progress is not treated as down.
/// </summary>
public static class HomeNavNotification
{
    public static bool ShouldShow(WorkerConnectionState state, bool selfUpdateInProgress) =>
        !selfUpdateInProgress && state == WorkerConnectionState.Offline;

    public static string Title(WorkerConnectionState state, bool selfUpdateInProgress) =>
        ShouldShow(state, selfUpdateInProgress) ? "Worker installation required" : "";
}
