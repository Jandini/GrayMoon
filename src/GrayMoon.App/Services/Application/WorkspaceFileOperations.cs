using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services.Application;

public sealed class WorkspaceFileOperations(
    WorkspaceRepository workspaceRepository,
    WorkspaceFileRepository fileRepository,
    WorkspaceService workspaceService,
    IAgentBridge agentBridge,
    WorkspaceFileVersionService fileVersionService,
    WorkspaceGitService workspaceGitService) : IWorkspaceFileOperations
{
    public async Task<List<WorkspaceFileDto>?> ListAsync(int workspaceId, CancellationToken cancellationToken)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return null;

        var files = await fileRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        return files.Select(f => new WorkspaceFileDto
        {
            FileId = f.FileId,
            WorkspaceId = f.WorkspaceId,
            RepositoryId = f.RepositoryId,
            RepositoryName = f.Repository?.RepositoryName,
            FileName = f.FileName,
            FilePath = f.FilePath,
            IsMissingOnDisk = f.IsMissingOnDisk == true
        }).ToList();
    }

    public async Task<(bool Found, int Added)> AddAsync(
        int workspaceId,
        IReadOnlyList<AddWorkspaceFileRequest> items,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return (false, 0);

        var repoIds = workspace.Repositories.Select(r => r.RepositoryId).ToHashSet();
        var accepted = new List<(int RepositoryId, string FileName, string FilePath)>();
        foreach (var item in items)
        {
            if (!repoIds.Contains(item.RepositoryId))
                continue;
            var fileName = (item.FileName ?? string.Empty).Trim();
            var filePath = (item.FilePath ?? string.Empty).Trim().Replace('\\', '/');
            if (fileName.Length == 0 || filePath.Length == 0)
                continue;
            accepted.Add((item.RepositoryId, fileName, filePath));
        }

        await fileRepository.AddRangeAsync(workspaceId, accepted, cancellationToken);
        return (true, accepted.Count);
    }

    public async Task<(bool Found, bool AgentConnected, AgentSearchFilesResponse? Data, string? Error)> SearchAsync(
        int workspaceId,
        string? pattern,
        string? repositoryName,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return (false, true, null, null);

        if (!agentBridge.IsAgentConnected)
            return (true, false, null, "Agent not connected. Start GrayMoon.Agent to search files.");

        var workspaceRoot = await workspaceService.GetRootPathForWorkspaceAsync(workspace, cancellationToken);
        var searchPattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
        var response = await agentBridge.SendCommandAsync("SearchFiles", new
        {
            workspaceName = workspace.Name,
            repositoryName = string.IsNullOrWhiteSpace(repositoryName) ? null : repositoryName.Trim(),
            searchPattern,
            workspaceRoot
        }, cancellationToken);

        if (!response.Success || response.Data == null)
            return (true, true, null, response.Error ?? "Search failed.");

        var data = AgentResponseJson.DeserializeAgentResponse<AgentSearchFilesResponse>(response.Data)
            ?? new AgentSearchFilesResponse { Files = [] };
        return (true, true, data, null);
    }

    public async Task<WorkspaceFileVersionUpdateResult> UpdateVersionsAsync(
        int workspaceId,
        CancellationToken cancellationToken,
        IReadOnlySet<int>? selectedRepositoryIds = null,
        bool filterPatternTokensToSelectedRepositories = true,
        bool commitUpdatedFiles = false,
        bool checkAfter = false,
        IProgress<OperationProgress>? progress = null)
    {
        progress.Report("Updating file versions...");
        var (updated, failed, error, updatedFiles) = await fileVersionService.UpdateAllVersionsAsync(
            workspaceId,
            selectedRepositoryIds: selectedRepositoryIds,
            filterPatternTokensToSelectedRepositories: filterPatternTokensToSelectedRepositories,
            cancellationToken: cancellationToken);

        var mappedFiles = updatedFiles
            .Select(f => new WorkspaceFileVersionUpdatedFile(f.RepositoryId, f.RepoName, f.FilePath))
            .ToList();

        if (error != null)
            return new WorkspaceFileVersionUpdateResult(updated, failed, error, mappedFiles);

        if (commitUpdatedFiles && updatedFiles.Count > 0)
        {
            progress.Report("Committing updated file versions...");
            var byRepo = updatedFiles
                .GroupBy(x => (x.RepositoryId, x.RepoName))
                .Select(g => (g.Key.RepositoryId, g.Key.RepoName, (IReadOnlyList<string>)g.Select(x => x.FilePath).Distinct().ToList()))
                .ToList();
            var commitResults = await workspaceGitService.CommitFilePathsAsync(
                workspaceId,
                byRepo,
                onProgress: (c, t, _) => progress.Report($"Committed version files {c} of {t}", c, t),
                cancellationToken: cancellationToken);
            foreach (var (_, _, errMsg) in commitResults)
            {
                if (!string.IsNullOrEmpty(errMsg))
                    return new WorkspaceFileVersionUpdateResult(updated, failed, errMsg, mappedFiles);
            }
        }

        if (checkAfter)
        {
            progress.Report("Checking file versions...");
            await fileVersionService.CheckAndPersistFileVersionStatusAsync(workspaceId, cancellationToken, forceFresh: true);
        }

        return new WorkspaceFileVersionUpdateResult(updated, failed, null, mappedFiles);
    }
}
