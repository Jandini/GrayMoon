using GrayMoon.App.Services.GitChanges;

namespace GrayMoon.App.Tests;

internal sealed class RecordingGitChangesNotifier : IWorkspaceGitChangesNotifier
{
    public List<(int WorkspaceId, int ContextId)> Published { get; } = [];

    public event Action<int, int>? Changed;

    public void Publish(int workspaceId, int contextId)
    {
        Published.Add((workspaceId, contextId));
        Changed?.Invoke(workspaceId, contextId);
    }
}
