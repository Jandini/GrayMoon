using GrayMoon.Common.Git;

namespace GrayMoon.App.Services.GitChanges;

public sealed class GitChangesStatusResult
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public GitChangeSnapshot? Snapshot { get; set; }
}

public sealed class GitChangesDiffResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public GitDiffDocument? Diff { get; set; }
}

public sealed class GitChangesMutationResult
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public GitChangeSnapshot? Snapshot { get; set; }
}

public sealed class GitChangesCommitResult
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? CommitSha { get; set; }
    public GitChangeSnapshot? Snapshot { get; set; }
}

/// <summary>
/// Thin wrapper over <see cref="IAgentBridge.SendCommandAsync"/> for the five Git Changes agent commands.
/// Callers resolve <paramref name="workspaceRoot"/>/<paramref name="workspaceName"/>/<paramref name="repositoryName"/>
/// themselves (same convention as every other Agent-bridged handler in the App) - this client only knows
/// the wire shape, not how to look up a workspace/repository.
/// </summary>
public interface IGitChangesAgentClient
{
    Task<GitChangesStatusResult> GetStatusAsync(
        string workspaceRoot, string workspaceName, string repositoryName,
        int workspaceId, int repositoryId, CancellationToken cancellationToken);

    Task<GitChangesDiffResult> GetDiffAsync(
        string workspaceRoot, string workspaceName, string repositoryName,
        string path, GitDiffComparison comparison, CancellationToken cancellationToken);

    Task<GitChangesMutationResult> StageAsync(
        string workspaceRoot, string workspaceName, string repositoryName,
        GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    Task<GitChangesMutationResult> UnstageAsync(
        string workspaceRoot, string workspaceName, string repositoryName,
        GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    Task<GitChangesCommitResult> CommitAsync(
        string workspaceRoot, string workspaceName, string repositoryName,
        string commitMessage, bool stageAllFirst, CancellationToken cancellationToken);
}

public sealed class GitChangesAgentClient(IAgentBridge agentBridge) : IGitChangesAgentClient
{
    public async Task<GitChangesStatusResult> GetStatusAsync(
        string workspaceRoot, string workspaceName, string repositoryName,
        int workspaceId, int repositoryId, CancellationToken cancellationToken)
    {
        var args = new { workspaceRoot, workspaceName, repositoryName, workspaceId, repositoryId };
        var response = await agentBridge.SendCommandAsync("GetGitChangeStatus", args, cancellationToken);
        return AgentResponseJson.DeserializeAgentResponse<GitChangesStatusResult>(response.Data)
            ?? new GitChangesStatusResult { Success = false, ErrorMessage = response.Error ?? "No response from agent." };
    }

    public async Task<GitChangesDiffResult> GetDiffAsync(
        string workspaceRoot, string workspaceName, string repositoryName,
        string path, GitDiffComparison comparison, CancellationToken cancellationToken)
    {
        var args = new { workspaceRoot, workspaceName, repositoryName, path, comparison = (int)comparison };
        var response = await agentBridge.SendCommandAsync("GetGitFileDiff", args, cancellationToken);
        return AgentResponseJson.DeserializeAgentResponse<GitChangesDiffResult>(response.Data)
            ?? new GitChangesDiffResult { Success = false, ErrorMessage = response.Error ?? "No response from agent." };
    }

    public async Task<GitChangesMutationResult> StageAsync(
        string workspaceRoot, string workspaceName, string repositoryName,
        GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var args = new { workspaceRoot, workspaceName, repositoryName, scope = (int)scope, paths };
        var response = await agentBridge.SendCommandAsync("StageGitChanges", args, cancellationToken);
        return AgentResponseJson.DeserializeAgentResponse<GitChangesMutationResult>(response.Data)
            ?? new GitChangesMutationResult { Success = false, ErrorMessage = response.Error ?? "No response from agent." };
    }

    public async Task<GitChangesMutationResult> UnstageAsync(
        string workspaceRoot, string workspaceName, string repositoryName,
        GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var args = new { workspaceRoot, workspaceName, repositoryName, scope = (int)scope, paths };
        var response = await agentBridge.SendCommandAsync("UnstageGitChanges", args, cancellationToken);
        return AgentResponseJson.DeserializeAgentResponse<GitChangesMutationResult>(response.Data)
            ?? new GitChangesMutationResult { Success = false, ErrorMessage = response.Error ?? "No response from agent." };
    }

    public async Task<GitChangesCommitResult> CommitAsync(
        string workspaceRoot, string workspaceName, string repositoryName,
        string commitMessage, bool stageAllFirst, CancellationToken cancellationToken)
    {
        var args = new { workspaceRoot, workspaceName, repositoryName, commitMessage, stageAllFirst };
        var response = await agentBridge.SendCommandAsync("CommitGitChanges", args, cancellationToken);
        return AgentResponseJson.DeserializeAgentResponse<GitChangesCommitResult>(response.Data)
            ?? new GitChangesCommitResult { Success = false, ErrorMessage = response.Error ?? "No response from agent." };
    }
}
