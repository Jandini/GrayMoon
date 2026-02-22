using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;

namespace GrayMoon.App.Services;

/// <summary>Runs file search via the agent for a workspace. Used by FileFoundModal.</summary>
public interface IWorkspaceFileSearchService
{
    Task<AgentSearchFilesResponse?> SearchAsync(int workspaceId, string? pattern, string? repositoryName, CancellationToken cancellationToken = default);
}

public sealed class WorkspaceFileSearchService(
    IAgentBridge agentBridge,
    WorkspaceRepository workspaceRepository) : IWorkspaceFileSearchService
{
    public async Task<AgentSearchFilesResponse?> SearchAsync(int workspaceId, string? pattern, string? repositoryName, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null || !agentBridge.IsAgentConnected)
            return null;

        var searchPattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
        var response = await agentBridge.SendCommandAsync("SearchFiles", new
        {
            workspaceName = workspace.Name,
            repositoryName = string.IsNullOrWhiteSpace(repositoryName) ? null : repositoryName.Trim(),
            searchPattern
        }, cancellationToken);

        if (!response.Success || response.Data == null)
            return null;

        return AgentResponseJson.DeserializeAgentResponse<AgentSearchFilesResponse>(response.Data)
            ?? new AgentSearchFilesResponse { Files = [] };
    }
}
