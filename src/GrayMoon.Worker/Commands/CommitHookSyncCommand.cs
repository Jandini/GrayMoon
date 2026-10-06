using System.Diagnostics;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Worker.Abstractions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Handles post-commit and post-update hooks: re-reads repository state, including the version and the
/// .csproj references when the workspace's profile asks for them.
/// No git fetch - uses the existing remote tracking refs from the last checkout/sync.
/// </summary>
public sealed class CommitHookSyncCommand(
    IRepositoryStateProbe stateProbe,
    IWorkspaceCapabilityProvider capabilityProvider,
    IHubConnectionProvider hubProvider,
    ILogger<CommitHookSyncCommand> logger)
{
    public async Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryPath))
        {
            logger.LogWarning("CommitHookSync job missing repositoryPath");
            return;
        }

        // There is no app request to carry capabilities on this path, so they are resolved here.
        var capabilities = await capabilityProvider.GetAsync(payload.WorkspaceId, cancellationToken);

        // One probe for version, branch/tag, commit counts, upstream and projects, so a commit reports the
        // same state groups a checkout does and the two can never drift apart.
        var (state, _) = await stateProbe.CaptureAsync(payload.RepositoryPath, new RepositoryStateProbeOptions
        {
            IncludeGitVersion = true,
            IncludeProjects = true,
            Capabilities = capabilities
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
                RepositoryPath = payload.RepositoryPath,
                Version = version,
                GitVersionFailed = state.GitVersionProbed && state.GitVersion == null,
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
            var syncSw = Stopwatch.StartNew();
            logger.LogDebug(
                "CommitHookSync invoking SyncCommand: workspace={WorkspaceId}, repo={RepoId}",
                payload.WorkspaceId, payload.RepositoryId);
            await connection.InvokeAsync(WorkerHubMethods.SyncCommand, notification, cancellationToken);
            logger.LogInformation(
                "CommitHookSync SyncCommand returned in {ElapsedMs}ms: workspace={WorkspaceId}, repo={RepoId}, version={Version}, branch={Branch}, \u2191{Outgoing} \u2193{Incoming}, hasUpstream={HasUpstream}",
                syncSw.ElapsedMilliseconds, payload.WorkspaceId, payload.RepositoryId, version, branch, state.OutgoingCommits, state.IncomingCommits, state.HasUpstream);
        }
        else
        {
            logger.LogWarning("Hub not connected, cannot send CommitHookSync SyncCommand");
        }
    }
}
