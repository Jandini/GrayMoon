using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Application.Workspaces;
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

/// <summary>
/// Thin wrapper over <see cref="IWorkerBridge.SendCommandAsync"/> for the five Git Changes worker commands.
/// Callers resolve <paramref name="workspaceRoot"/>/<paramref name="workspaceName"/>/<paramref name="repositoryName"/>
/// themselves (same convention as every other Worker-bridged handler in the App) - this client only knows
/// the wire shape, not how to look up a workspace/repository.
/// </summary>
public interface IGitChangesWorkerClient
{
    Task<GitChangesStatusResult> GetStatusAsync(
        string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
        int workspaceId, int repositoryId, CancellationToken cancellationToken,
        bool includeLineStats = false);

    Task<GitChangesDiffResult> GetDiffAsync(
        string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
        string path, GitDiffComparison comparison, CancellationToken cancellationToken);

    Task<GitChangesMutationResult> StageAsync(
        string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
        GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    Task<GitChangesMutationResult> UnstageAsync(
        string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
        GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    /// <summary>Discards unstaged changes only: restores tracked working-tree edits from the index and
    /// deletes untracked files - never touches staged/index content.</summary>
    Task<GitChangesMutationResult> DiscardAsync(
        string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
        GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    Task<GitChangesCommitResult> CommitAsync(
        string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
        string commitMessage, bool stageAllFirst, CancellationToken cancellationToken);
}

public sealed class GitChangesWorkerClient(
    IWorkerBridge workerBridge,
    IWorkspaceCapabilitiesResolver capabilitiesResolver) : IGitChangesWorkerClient
{
    public async Task<GitChangesStatusResult> GetStatusAsync(
        string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
        int workspaceId, int repositoryId, CancellationToken cancellationToken,
        bool includeLineStats = false)
    {
        var capabilities = await ResolveCapabilitiesAsync(workspaceId, cancellationToken);
        var args = new { workspaceRoot, workspaceRepositoryName, workspaceName, repositoryName, workspaceId, repositoryId, includeLineStats, capabilities };
        var response = await workerBridge.SendCommandAsync("GetGitChangeStatus", args, cancellationToken);
        return WorkerResponseJson.DeserializeWorkerResponse<GitChangesStatusResult>(response.Data)
            ?? new GitChangesStatusResult { Success = false, ErrorMessage = response.Error ?? "No response from worker." };
    }

    public async Task<GitChangesDiffResult> GetDiffAsync(
        string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
        string path, GitDiffComparison comparison, CancellationToken cancellationToken)
    {
        var args = new { workspaceRoot, workspaceRepositoryName, workspaceName, repositoryName, path, comparison = (int)comparison };
        var response = await workerBridge.SendCommandAsync("GetGitFileDiff", args, cancellationToken);
        return WorkerResponseJson.DeserializeWorkerResponse<GitChangesDiffResult>(response.Data)
            ?? new GitChangesDiffResult { Success = false, ErrorMessage = response.Error ?? "No response from worker." };
    }

    public async Task<GitChangesMutationResult> StageAsync(
        string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
        GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var args = new { workspaceRoot, workspaceRepositoryName, workspaceName, repositoryName, scope = (int)scope, paths };
        var response = await workerBridge.SendCommandAsync("StageGitChanges", args, cancellationToken);
        return WorkerResponseJson.DeserializeWorkerResponse<GitChangesMutationResult>(response.Data)
            ?? new GitChangesMutationResult { Success = false, ErrorMessage = response.Error ?? "No response from worker." };
    }

    public async Task<GitChangesMutationResult> UnstageAsync(
        string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
        GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var args = new { workspaceRoot, workspaceRepositoryName, workspaceName, repositoryName, scope = (int)scope, paths };
        var response = await workerBridge.SendCommandAsync("UnstageGitChanges", args, cancellationToken);
        return WorkerResponseJson.DeserializeWorkerResponse<GitChangesMutationResult>(response.Data)
            ?? new GitChangesMutationResult { Success = false, ErrorMessage = response.Error ?? "No response from worker." };
    }

    public async Task<GitChangesMutationResult> DiscardAsync(
        string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
        GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var args = new { workspaceRoot, workspaceRepositoryName, workspaceName, repositoryName, scope = (int)scope, paths };
        var response = await workerBridge.SendCommandAsync("DiscardGitChanges", args, cancellationToken);
        return WorkerResponseJson.DeserializeWorkerResponse<GitChangesMutationResult>(response.Data)
            ?? new GitChangesMutationResult { Success = false, ErrorMessage = response.Error ?? "No response from worker." };
    }

    public async Task<GitChangesCommitResult> CommitAsync(
        string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
        string commitMessage, bool stageAllFirst, CancellationToken cancellationToken)
    {
        var args = new { workspaceRoot, workspaceRepositoryName, workspaceName, repositoryName, commitMessage, stageAllFirst };
        var response = await workerBridge.SendCommandAsync("CommitGitChanges", args, cancellationToken);
        return WorkerResponseJson.DeserializeWorkerResponse<GitChangesCommitResult>(response.Data)
            ?? new GitChangesCommitResult { Success = false, ErrorMessage = response.Error ?? "No response from worker." };
    }

    /// <summary>Null when the workspace is gone, which the worker reads as "not stated".</summary>
    private async Task<RepositoryOperationCapabilities?> ResolveCapabilitiesAsync(int workspaceId, CancellationToken cancellationToken)
    {
        try
        {
            return (await capabilitiesResolver.GetAsync(workspaceId, cancellationToken)).ToRepositoryOperationCapabilities();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
