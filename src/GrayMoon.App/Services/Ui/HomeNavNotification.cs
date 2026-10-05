using GrayMoon.App.Services.Worker;

namespace GrayMoon.App.Services.Ui;

/// <summary>
/// When the Home nav notification dot is shown. Worker update matches the worktree branch;
/// install, prerequisites, and connectors are the extra cases on this branch.
/// </summary>
public static class HomeNavNotification
{
    public static bool ShouldShow(
        WorkerConnectionState state,
        bool selfUpdateInProgress,
        bool hostPrerequisitesMissing,
        bool connectorsRequired) =>
        connectorsRequired || WorkerRequiresAttention(state, selfUpdateInProgress, hostPrerequisitesMissing);

    public static bool WorkerRequiresAttention(
        WorkerConnectionState state,
        bool selfUpdateInProgress,
        bool hostPrerequisitesMissing)
    {
        if (selfUpdateInProgress)
            return false;

        return state switch
        {
            WorkerConnectionState.VersionMismatch => true,
            WorkerConnectionState.Offline => true,
            WorkerConnectionState.Online => hostPrerequisitesMissing,
            _ => false
        };
    }

    /// <summary>
    /// Matches Home's red Connectors button: none configured, or a connector in use is unhealthy.
    /// </summary>
    public static bool ConnectorsRequired(bool hasConnectors, bool anyUsedConnectorUnhealthy) =>
        !hasConnectors || anyUsedConnectorUnhealthy;

    public static string Title(
        WorkerConnectionState state,
        bool selfUpdateInProgress,
        bool hostPrerequisitesMissing,
        bool hasConnectors,
        bool anyUsedConnectorUnhealthy)
    {
        var parts = new List<string>(2);
        if (WorkerRequiresAttention(state, selfUpdateInProgress, hostPrerequisitesMissing))
        {
            parts.Add(state switch
            {
                WorkerConnectionState.VersionMismatch => "Worker update available",
                WorkerConnectionState.Offline => "Worker installation required",
                _ => "Worker prerequisites required"
            });
        }

        if (!hasConnectors)
            parts.Add("Connector required");
        else if (anyUsedConnectorUnhealthy)
            parts.Add("Unhealthy connector in use");

        return string.Join(". ", parts);
    }
}
