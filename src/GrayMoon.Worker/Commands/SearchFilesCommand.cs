using GrayMoon.Worker.Services;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Commands;

public sealed class SearchFilesCommand(IWorkspaceFileSearchService fileSearch) : ICommandHandler<SearchFilesRequest, SearchFilesResponse>
{
    public async Task<SearchFilesResponse> ExecuteAsync(SearchFilesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        if (string.IsNullOrEmpty(workspacePath) || !Directory.Exists(workspacePath))
            return new SearchFilesResponse { Files = [] };

        var files = await fileSearch.SearchAsync(
            workspacePath,
            request.RepositoryName,
            request.SearchPattern ?? "*",
            request.WorkspaceRepositoryName,
            cancellationToken);

        // Only text files can be version files; sniffing lazily keeps the cost to the files actually returned.
        return new SearchFilesResponse
        {
            Files = files
                .Where(f => TextFileGuard.GetRejectionReason(Path.Combine(
                    WorkerRepositoryPaths.Resolve(workspacePath, f.RepositoryName, request.WorkspaceRepositoryName),
                    f.FilePath.Replace('/', Path.DirectorySeparatorChar))) is null)
                .Take(20)
                .ToArray()
        };
    }
}
