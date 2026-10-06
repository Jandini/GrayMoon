using GrayMoon.Abstractions.Workspaces;

namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// Supplies the workspace capabilities a Worker-initiated operation has to act on, for the one direction of
/// traffic that has no app request to read them from: the four git hooks, where the trigger is the
/// developer's own <c>git</c> invocation.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <c>IWorkerTokenProvider</c>: per-entity configuration the Worker cannot know, fetched from
/// the app's HTTP API and cached, never read from app persistence.
/// </para>
/// <para>
/// Resolution is keyed by workspace id only. Worktree-backed Features inherit the parent Workspace's
/// profile, so there is deliberately no context-shaped overload.
/// </para>
/// </remarks>
public interface IWorkspaceCapabilityProvider
{
    /// <summary>
    /// Capabilities for <paramref name="workspaceId"/>. A workspace whose capabilities cannot be learned
    /// falls back to full enrichment rather than failing, so a hook sync is never lost to an unreachable
    /// app. Only cancellation propagates.
    /// </summary>
    Task<RepositoryOperationCapabilities> GetAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the capabilities an app-initiated command carried, so the hook paths for that workspace cost
    /// no HTTP at all. A null <paramref name="capabilities"/> - a command from an app that predates
    /// workspace profiles - is ignored rather than cached, since it states nothing.
    /// </summary>
    void Remember(int workspaceId, RepositoryOperationCapabilities? capabilities);

    /// <summary>Drops the cached capabilities for one workspace, so the next resolution refetches them.</summary>
    void Invalidate(int workspaceId);
}
