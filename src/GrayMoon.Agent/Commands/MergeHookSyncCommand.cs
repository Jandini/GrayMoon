using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Agent.Abstractions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

/// <summary>
/// Handles post-merge hooks: re-runs GitVersion and gets commit counts.
/// No git fetch — the merge already brought remote changes in; existing remote tracking refs are current enough.
/// </summary>
public sealed class MergeHookSyncCommand(IGitService git, ICsProjFileService csProjFileService, IAgentTokenProvider tokenProvider, IHubConnectionProvider hubProvider, ILogger<MergeHookSyncCommand> logger)
{
    public async Task ExecuteAsync(INotifyJob payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.RepositoryPath))
        {
            logger.LogWarning("MergeHookSync job missing repositoryPath");
            return;
        }

        var (versionResult, _) = await git.GetVersionAsync(payload.RepositoryPath, cancellationToken);
        var version = versionResult?.InformationalVersion ?? "-";
        var branch = versionResult?.BranchName ?? versionResult?.EscapedBranchName ?? "-";
        var findProjectsTask = csProjFileService.FindAsync(payload.RepositoryPath, cancellationToken);

        int? outgoing = null;
        int? incoming = null;
        int? defaultBehind = null;
        int? defaultAhead = null;
        if (branch != "-")
        {
            var (o, i, _) = await git.GetCommitCountsAsync(payload.RepositoryPath, branch, null, cancellationToken);
            outgoing = o;
            incoming = i;
            var (db, da, _) = await git.GetCommitCountsVsDefaultAsync(payload.RepositoryPath, null, cancellationToken);
            defaultBehind = db;
            defaultAhead = da;
        }

        bool? hasUpstream = null;
        if (branch != "-")
        {
            string? token = await tokenProvider.GetTokenForRepositoryAsync(payload.RepositoryId, cancellationToken);
            if (token == null)
            {
                logger.LogDebug("MergeHookSync: no token available for repo {RepositoryId}; skipping remote branch query.", payload.RepositoryId);
            }
            else
            {
                var remoteBranches = await git.GetRemoteBranchesAsync(payload.RepositoryPath, token, cancellationToken);
                hasUpstream = remoteBranches.Any(r => string.Equals(r, branch, StringComparison.OrdinalIgnoreCase));
            }
        }

        var projects = await findProjectsTask;
        var syncProjects = projects?
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new RepositorySyncProjectNotification
            {
                Name = p.Name.Trim(),
                ProjectType = (int)p.ProjectType,
                ProjectPath = p.ProjectPath ?? "",
                TargetFramework = p.TargetFramework ?? "",
                PackageId = p.PackageId,
                PackageReferences = p.PackageReferences
                    .Where(pr => !string.IsNullOrWhiteSpace(pr.Name))
                    .Select(pr => new RepositorySyncPackageReferenceNotification
                    {
                        Name = pr.Name.Trim(),
                        Version = pr.Version ?? ""
                    })
                    .ToList()
            })
            .ToList();

        var connection = hubProvider.Connection;
        if (connection?.State == HubConnectionState.Connected)
        {
            var notification = new RepositorySyncNotification
            {
                WorkspaceId = payload.WorkspaceId,
                RepositoryId = payload.RepositoryId,
                Version = version,
                Branch = branch,
                OutgoingCommits = outgoing,
                IncomingCommits = incoming,
                HasUpstream = hasUpstream,
                DefaultBranchBehind = defaultBehind,
                DefaultBranchAhead = defaultAhead,
                Projects = syncProjects,
                ErrorMessage = null
            };
            await connection.InvokeAsync(AgentHubMethods.SyncCommand, notification, cancellationToken);
            logger.LogInformation("MergeHookSync sent: workspace={WorkspaceId}, repo={RepoId}, version={Version}, branch={Branch}, ↑{Outgoing} ↓{Incoming}, hasUpstream={HasUpstream}",
                payload.WorkspaceId, payload.RepositoryId, version, branch, outgoing, incoming, hasUpstream);
        }
        else
        {
            logger.LogWarning("Hub not connected, cannot send MergeHookSync SyncCommand");
        }
    }
}
