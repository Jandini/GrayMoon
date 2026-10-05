using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Commands;

public sealed class SearchFilesCommand(
    IGitService git,
    IWorkspaceFileSearchService fileSearch) : ICommandHandler<SearchFilesRequest, SearchFilesResponse>
{
    public async Task<SearchFilesResponse> ExecuteAsync(SearchFilesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        if (string.IsNullOrEmpty(workspacePath) || !Directory.Exists(workspacePath))
            return new SearchFilesResponse { Files = [] };

        var files = await fileSearch.SearchAsync(
            workspacePath,
            request.RepositoryName,
            request.SearchPattern ?? "*",
            cancellationToken);

        return new SearchFilesResponse
        {
            Files = files.Take(20).ToArray()
        };
    }
}
