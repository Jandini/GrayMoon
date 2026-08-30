using GrayMoon.App.Models.Api;

namespace GrayMoon.Application;

public sealed class WorkspaceFileDto
{
    public int FileId { get; set; }
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
    public string? RepositoryName { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public bool IsMissingOnDisk { get; set; }
}

public sealed class AddWorkspaceFileRequest
{
    public int RepositoryId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
}

public interface IWorkspaceFileOperations
{
    Task<List<WorkspaceFileDto>?> ListAsync(int workspaceId, CancellationToken cancellationToken);

    Task<(bool Found, int Added)> AddAsync(int workspaceId, IReadOnlyList<AddWorkspaceFileRequest> items, CancellationToken cancellationToken);

    Task<(bool Found, bool AgentConnected, AgentSearchFilesResponse? Data, string? Error)> SearchAsync(
        int workspaceId,
        string? pattern,
        string? repositoryName,
        CancellationToken cancellationToken);

    Task<(int Updated, int Failed, string? Error)> UpdateVersionsAsync(int workspaceId, CancellationToken cancellationToken);
}
