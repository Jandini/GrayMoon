using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Ui;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Ci;

/// <summary>
/// The provider for <see cref="WorkspaceCiProvider.None"/>: no CI query, no refresh, no persistence and no
/// push-time run watching. Stateless, so one shared instance serves every workspace.
/// </summary>
public sealed class NoCiProvider : IWorkspaceCiProvider
{
    public static NoCiProvider Instance { get; } = new();

    private static readonly IReadOnlyDictionary<int, RepositoryActionsPersistedState> Empty =
        new Dictionary<int, RepositoryActionsPersistedState>();

    private NoCiProvider()
    {
    }

    public WorkspaceCiProvider Kind => WorkspaceCiProvider.None;

    public bool IsEnabled => false;

    public Task<IReadOnlyDictionary<int, RepositoryActionsPersistedState>> GetPersistedStatusesAsync(
        int workspaceId,
        WorkspaceFeatureContextId? featureContextId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Empty);

    public Task<IReadOnlyList<ActionStatusInfo>?> RefreshStatusesAsync(
        WorkspaceFeatureContextId? featureContextId,
        int workspaceRepositoryId,
        GitHubRepositoryEntry repository,
        string branch,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ActionStatusInfo>?>(null);

    public IPushCiRunWatch CreatePushRunWatch(OverlayCommandTerminalService? overlayTerminal) => NoOpPushCiRunWatch.Instance;
}

/// <summary>Run watcher that never does anything. Used for CI=None and when there is no overlay to stream into.</summary>
public sealed class NoOpPushCiRunWatch : IPushCiRunWatch
{
    public static NoOpPushCiRunWatch Instance { get; } = new();

    private NoOpPushCiRunWatch()
    {
    }

    public Task TickAsync(
        IReadOnlyList<PushRepoPayload> pushedRepos,
        IReadOnlyList<WorkspaceRepositoryLink> links,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}
