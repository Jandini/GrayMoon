using GrayMoon.App.Models;

namespace GrayMoon.App.Services.Queries;

public sealed record WorkspaceProjectListCursor(int ProjectTypeSortKey, string ProjectName, int ProjectId);

public sealed record WorkspaceProjectListRequest(
    int WorkspaceId,
    string? Search,
    int PageSize,
    WorkspaceProjectListCursor? Cursor);

public sealed record WorkspaceProjectListItemDto(
    int ProjectId,
    string ProjectName,
    ProjectType ProjectType,
    string TargetFramework,
    string ProjectFilePath);

public sealed record WorkspaceProjectListPageResult(
    IReadOnlyList<WorkspaceProjectListItemDto> Items,
    WorkspaceProjectListCursor? NextCursor,
    bool HasMore);

public sealed record WorkspaceProjectListFilter(int WorkspaceId, string? Search);
