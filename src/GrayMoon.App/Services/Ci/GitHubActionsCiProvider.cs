using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Ui;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Ci;

/// <summary>
/// The provider for <see cref="WorkspaceCiProvider.GitHubActions"/>. A thin adapter over the existing,
/// deliberately GitHub-specific services - <see cref="WorkspaceActionService"/> (fetch, coalesce, persist),
/// <see cref="GitHubActionsService"/> (workflow statuses) and <see cref="GhaWorkflowLiveFeedService"/> (run jobs
/// feed) - so behaviour is exactly what those services did before the boundary existed.
/// </summary>
public sealed class GitHubActionsCiProvider(
    WorkspaceActionService actionService,
    GitHubActionsService gitHubActionsService,
    GhaWorkflowLiveFeedService liveFeedService,
    ILogger<GitHubActionsCiProvider> logger) : IWorkspaceCiProvider
{
    public WorkspaceCiProvider Kind => WorkspaceCiProvider.GitHubActions;

    public bool IsEnabled => true;

    public Task<IReadOnlyDictionary<int, RepositoryActionsPersistedState>> GetPersistedStatusesAsync(
        int workspaceId,
        WorkspaceFeatureContextId? featureContextId,
        CancellationToken cancellationToken = default)
        => featureContextId is { } contextId
            ? actionService.GetPersistedActionsForWorkspaceContextAsync(workspaceId, contextId.Value, cancellationToken)
            : actionService.GetPersistedActionsForWorkspaceAsync(workspaceId, cancellationToken);

    public Task<IReadOnlyList<ActionStatusInfo>?> RefreshStatusesAsync(
        WorkspaceFeatureContextId? featureContextId,
        int workspaceRepositoryId,
        GitHubRepositoryEntry repository,
        string branch,
        CancellationToken cancellationToken = default)
        => featureContextId is { } contextId
            ? actionService.FetchAndPersistContextAsync(contextId.Value, workspaceRepositoryId, repository, branch, cancellationToken)
            : actionService.FetchAndPersistAsync(workspaceRepositoryId, repository, branch, cancellationToken);

    public IPushCiRunWatch CreatePushRunWatch(OverlayCommandTerminalService? overlayTerminal)
        => overlayTerminal == null
            ? NoOpPushCiRunWatch.Instance
            : new GitHubActionsPushRunWatch(gitHubActionsService, liveFeedService, overlayTerminal, logger);
}
