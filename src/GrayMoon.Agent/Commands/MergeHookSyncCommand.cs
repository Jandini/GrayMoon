using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Agent.Abstractions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

/// <summary>
/// Handles post-merge hooks: re-runs GitVersion and gets commit counts.
/// No git fetch - the merge already brought remote changes in; existing remote tracking refs are current enough.
/// </summary>
public sealed class MergeHookSyncCommand(IRepositoryStateProbe stateProbe, IHubConnectionProvider hubProvider, ILogger<MergeHookSyncCommand> logger)
{
    public async Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryPath))
        {
            logger.LogWarning("MergeHookSync job missing repositoryPath");
            return;
        }

        // One probe for version, branch/tag, commit counts, upstream and projects, so a merge reports the
        // same state groups a checkout does - including the comparison against the default branch, which a
        // pull moves just as much as the comparison against the upstream.
        var (state, _) = await stateProbe.CaptureAsync(payload.RepositoryPath, new RepositoryStateProbeOptions
        {
            IncludeGitVersion = true,
            IncludeProjects = true
        }, cancellationToken);

        var version = state.GitVersion ?? "-";
        var branch = state.BranchName ?? "-";

        var connection = hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
        {
            var notification = new RepositorySyncNotification
            {
                WorkspaceId = payload.WorkspaceId,
                RepositoryId = payload.RepositoryId,
                Version = version,
                Branch = branch,
                Tag = state.CheckedOutTag,
                OutgoingCommits = state.OutgoingCommits,
                IncomingCommits = state.IncomingCommits,
                HasUpstream = state.HasUpstream,
                DefaultBranchBehind = state.DefaultBranchBehind,
                DefaultBranchAhead = state.DefaultBranchAhead,
                Projects = state.Projects,
                ErrorMessage = null,
                State = state
            };
            await connection.InvokeAsync(AgentHubMethods.SyncCommand, notification, cancellationToken);
            logger.LogInformation("MergeHookSync sent: workspace={WorkspaceId}, repo={RepoId}, version={Version}, branch={Branch}, ↑{Outgoing} ↓{Incoming}, hasUpstream={HasUpstream}",
                payload.WorkspaceId, payload.RepositoryId, version, branch, state.OutgoingCommits, state.IncomingCommits, state.HasUpstream);
        }
        else
        {
            logger.LogWarning("Hub not connected, cannot send MergeHookSync SyncCommand");
        }
    }
}
