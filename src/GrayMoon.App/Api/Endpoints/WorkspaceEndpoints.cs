using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GrayMoon.App.Api.Endpoints;

public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/workspaces/{workspaceId:int}").WithTags("Workspaces");

        group.MapGet("/files", GetWorkspaceFiles)
            .WithName("GetWorkspaceFiles");
        group.MapPost("/files", PostWorkspaceFiles)
            .WithName("PostWorkspaceFiles");
        group.MapGet("/files/search", SearchWorkspaceFiles)
            .WithName("SearchWorkspaceFiles");

        return routes;
    }

    private static async Task<Results<Ok<List<WorkspaceFileDto>>, NotFound>> GetWorkspaceFiles(
        [FromRoute] int workspaceId,
        [FromServices] WorkspaceFileRepository fileRepository,
        CancellationToken cancellationToken)
    {
        var files = await fileRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        var dtos = files.Select(f => new WorkspaceFileDto
        {
            FileId = f.FileId,
            WorkspaceId = f.WorkspaceId,
            RepositoryId = f.RepositoryId,
            RepositoryName = f.Repository?.RepositoryName,
            FileName = f.FileName,
            FilePath = f.FilePath
        }).ToList();
        return TypedResults.Ok(dtos);
    }

    private static async Task<Results<Ok<object>, BadRequest<ProblemDetails>, NotFound>> PostWorkspaceFiles(
        [FromRoute] int workspaceId,
        [FromBody] List<AddWorkspaceFileRequest> body,
        [FromServices] WorkspaceRepository workspaceRepository,
        [FromServices] WorkspaceFileRepository fileRepository,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return TypedResults.NotFound();

        var repoIds = workspace.Repositories.Select(r => r.RepositoryId).ToHashSet();
        var items = new List<(int RepositoryId, string FileName, string FilePath)>();
        foreach (var item in body ?? [])
        {
            if (!repoIds.Contains(item.RepositoryId))
                continue;
            var fileName = (item.FileName ?? string.Empty).Trim();
            var filePath = (item.FilePath ?? string.Empty).Trim().Replace('\\', '/');
            if (fileName.Length == 0 || filePath.Length == 0)
                continue;
            items.Add((item.RepositoryId, fileName, filePath));
        }

        await fileRepository.AddRangeAsync(workspaceId, items, cancellationToken);
        return TypedResults.Ok<object>(new { added = items.Count });
    }

    private static async Task<Results<Ok<AgentSearchFilesResponse>, BadRequest<ProblemDetails>, NotFound>> SearchWorkspaceFiles(
        [FromRoute] int workspaceId,
        [FromQuery] string? pattern,
        [FromQuery] string? repositoryName,
        [FromServices] WorkspaceRepository workspaceRepository,
        [FromServices] WorkspaceService workspaceService,
        [FromServices] IAgentBridge agentBridge,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null)
            return TypedResults.NotFound();

        if (!agentBridge.IsAgentConnected)
            return TypedResults.BadRequest(new ProblemDetails { Title = "Agent not connected. Start GrayMoon.Agent to search files." });

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
            return TypedResults.BadRequest(new ProblemDetails { Title = response.Error ?? "Search failed." });

        var data = AgentResponseJson.DeserializeAgentResponse<AgentSearchFilesResponse>(response.Data);
        data ??= new AgentSearchFilesResponse { Files = [] };
        return TypedResults.Ok(data);
    }
}

public sealed class WorkspaceFileDto
{
    public int FileId { get; set; }
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
    public string? RepositoryName { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
}

public sealed class AddWorkspaceFileRequest
{
    public int RepositoryId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
}
