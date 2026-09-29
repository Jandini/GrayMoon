using GrayMoon.App.Services.Agent;

namespace GrayMoon.App.Services.Ui;

/// <summary>
/// When the Home nav notification dot is shown. Worker update matches the worktree branch;
/// install, prerequisites, and connectors are the extra cases on this branch.
/// </summary>
public static class HomeNavNotification
{
    public static bool ShouldShow(
        AgentConnectionState state,
        bool selfUpdateInProgress,
        bool hostPrerequisitesMissing,
        bool connectorsRequired) =>
        connectorsRequired || WorkerRequiresAttention(state, selfUpdateInProgress, hostPrerequisitesMissing);

    public static bool WorkerRequiresAttention(
        AgentConnectionState state,
        bool selfUpdateInProgress,
        bool hostPrerequisitesMissing)
    {
        if (selfUpdateInProgress)
            return false;

        return state switch
        {
            AgentConnectionState.VersionMismatch => true,
            AgentConnectionState.Offline => true,
            AgentConnectionState.Online => hostPrerequisitesMissing,
            _ => false
        };
    }

    /// <summary>
    /// Matches Home's red Connectors button: none configured, or a connector in use is unhealthy.
    /// </summary>
    public static bool ConnectorsRequired(bool hasConnectors, bool anyUsedConnectorUnhealthy) =>
        !hasConnectors || anyUsedConnectorUnhealthy;

    public static string Title(
        AgentConnectionState state,
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
                AgentConnectionState.VersionMismatch => "Worker update available",
                AgentConnectionState.Offline => "Worker installation required",
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
