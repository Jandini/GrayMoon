namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// In-process fan-out for persisted Git Changes projection changes, so circuit components (the
/// Changes nav dot) can react without opening their own SignalR connection back to the hub.
/// Publishers run on the write-queue worker or a request thread - subscribers must marshal with
/// <c>InvokeAsync</c>.
/// </summary>
public interface IWorkspaceGitChangesNotifier
{
    /// <summary>Context id meaning "every context of the workspace" (e.g. repositories unlinked).</summary>
    const int AllContexts = 0;

    event Action<int, int>? Changed;

    void Publish(int workspaceId, int contextId);
}

public sealed class WorkspaceGitChangesNotifier(ILogger<WorkspaceGitChangesNotifier> logger) : IWorkspaceGitChangesNotifier
{
    public event Action<int, int>? Changed;

    public void Publish(int workspaceId, int contextId)
    {
        var handlers = Changed;
        if (handlers == null)
            return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action<int, int>>())
        {
            try
            {
                handler(workspaceId, contextId);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "Git Changes notifier subscriber failed for workspace {WorkspaceId} context {ContextId}",
                    workspaceId, contextId);
            }
        }
    }
}
