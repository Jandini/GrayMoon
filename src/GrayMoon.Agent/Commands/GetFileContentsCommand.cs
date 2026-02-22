using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class GetFileContentsCommand(IGitService git) : ICommandHandler<GetFileContentsRequest, GetFileContentsResponse>
{
    public async Task<GetFileContentsResponse> ExecuteAsync(GetFileContentsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var filePath = request.FilePath ?? throw new ArgumentException("filePath required");

        var workspacePath = git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);
        var fullFilePath = Path.Combine(repoPath, filePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(fullFilePath))
            return new GetFileContentsResponse { ErrorMessage = $"File not found: {filePath}" };

        var content = await File.ReadAllTextAsync(fullFilePath, cancellationToken);
        return new GetFileContentsResponse { Content = content };
    }
}
