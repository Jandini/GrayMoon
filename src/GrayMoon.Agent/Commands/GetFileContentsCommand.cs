using System.Text;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Common.Git;

namespace GrayMoon.Agent.Commands;

public sealed class GetFileContentsCommand(IGitService git) : ICommandHandler<GetFileContentsRequest, GetFileContentsResponse>
{
    private const long MaxBase64Bytes = 2 * 1024 * 1024;

    public async Task<GetFileContentsResponse> ExecuteAsync(GetFileContentsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var filePath = request.FilePath ?? throw new ArgumentException("filePath required");
        var workspaceRoot = request.WorkspaceRoot ?? throw new ArgumentException("workspaceRoot required");

        var workspacePath = git.GetWorkspacePath(workspaceRoot, workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);

        var validation = GitRepositoryPathValidator.Validate(repoPath, filePath);
        if (!validation.IsValid || validation.FullPath is null)
        {
            return new GetFileContentsResponse { ErrorMessage = validation.ErrorMessage ?? $"Invalid path: {filePath}" };
        }

        var fullFilePath = validation.FullPath;
        if (!File.Exists(fullFilePath))
        {
            return new GetFileContentsResponse { ErrorMessage = $"File not found: {filePath}" };
        }

        if (request.AsBase64)
        {
            var info = new FileInfo(fullFilePath);
            if (info.Length > MaxBase64Bytes)
            {
                return new GetFileContentsResponse { ErrorMessage = $"File too large to embed ({info.Length} bytes)." };
            }

            var bytes = await File.ReadAllBytesAsync(fullFilePath, cancellationToken);
            return new GetFileContentsResponse
            {
                ContentBase64 = Convert.ToBase64String(bytes),
                ContentType = GuessContentType(fullFilePath),
            };
        }

        var content = await File.ReadAllTextAsync(fullFilePath, Encoding.UTF8, cancellationToken);
        return new GetFileContentsResponse { Content = content };
    }

    private static string GuessContentType(string fullPath)
    {
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream",
        };
    }
}
