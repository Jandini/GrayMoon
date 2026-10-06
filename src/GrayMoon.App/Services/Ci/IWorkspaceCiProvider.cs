using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Ui;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Ci;

/// <summary>
/// CI behaviour for one workspace, selected once from its persisted <see cref="WorkspaceCiProvider"/> by
/// <see cref="IWorkspaceCiProviderResolver"/>. Callers ask the provider instead of checking the enum, so a
/// workspace with no CI provider performs no CI query, refresh or persistence, and a later provider is one
/// more implementation rather than a new branch at every call site.
/// </summary>
/// <remarks>
/// This is the CI boundary only. GitHub source control - repositories, pull requests (including their
/// check-run summary), connectors and tokens - does not go through it and works the same with or without CI.
/// </remarks>
public interface IWorkspaceCiProvider
{
    WorkspaceCiProvider Kind { get; }

    /// <summary>
    /// The workspace has CI status at all: the CI page, CI status refresh and run watching during
    /// synchronized push. Always equal to <c>WorkspaceCapabilities.UsesCiIntegration</c>.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Persisted per-workflow status keyed by RepositoryId. <paramref name="featureContextId"/> is null for the
    /// special Workspace (reads the legacy link rows) and set for a Feature context (reads that context's rows only).
    /// </summary>
    Task<IReadOnlyDictionary<int, RepositoryActionsPersistedState>> GetPersistedStatusesAsync(
        int workspaceId,
        WorkspaceFeatureContextId? featureContextId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches live status for <paramref name="branch"/> and persists it for the context (same null/set rule as
    /// <see cref="GetPersistedStatusesAsync"/>). Returns null when nothing was fetched or persisted.
    /// </summary>
    Task<IReadOnlyList<ActionStatusInfo>?> RefreshStatusesAsync(
        WorkspaceFeatureContextId? featureContextId,
        int workspaceRepositoryId,
        GitHubRepositoryEntry repository,
        string branch,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the per-level run watcher used while synchronized push waits for packages. Output goes to
    /// <paramref name="overlayTerminal"/>; without one there is nowhere to stream, so a no-op watcher is returned.
    /// </summary>
    IPushCiRunWatch CreatePushRunWatch(OverlayCommandTerminalService? overlayTerminal);
}
