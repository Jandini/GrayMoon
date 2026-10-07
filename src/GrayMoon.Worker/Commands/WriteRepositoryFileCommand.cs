using System.Text;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Writes one text file into a repository working tree (UTF-8, no BOM) through a temp file in the same
/// directory so a crash never leaves a half-written file. Performs no git operation.
/// </summary>
public sealed class WriteRepositoryFileCommand()
    : ICommandHandler<WriteRepositoryFileRequest, WriteRepositoryFileResponse>
{
    private const string TempSuffix = ".graymoon-tmp";

    public async Task<WriteRepositoryFileResponse> ExecuteAsync(WriteRepositoryFileRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var filePath = request.FilePath ?? throw new ArgumentException("filePath required");
        var workspaceRoot = request.WorkspaceRoot ?? throw new ArgumentException("workspaceRoot required");
        var content = request.Content ?? throw new ArgumentException("content required");

        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(workspaceRoot, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

        var validation = GitRepositoryPathValidator.Validate(repoPath, filePath);
        if (!validation.IsValid || validation.FullPath is null || validation.NormalizedRelativePath is null)
        {
            return new WriteRepositoryFileResponse { ErrorMessage = validation.ErrorMessage ?? $"Invalid path: {filePath}" };
        }

        var firstSegment = validation.NormalizedRelativePath.Split('/')[0];
        if (string.Equals(firstSegment, ".git", StringComparison.OrdinalIgnoreCase))
        {
            return new WriteRepositoryFileResponse { ErrorMessage = "Writing under .git is not allowed." };
        }

        var target = validation.FullPath;
        var bytes = Encoding.UTF8.GetBytes(content);

        try
        {
            if (request.OnlyIfChanged && File.Exists(target))
            {
                var existing = await File.ReadAllBytesAsync(target, cancellationToken);
                if (existing.AsSpan().SequenceEqual(bytes))
                {
                    return new WriteRepositoryFileResponse { Success = true, Written = false };
                }
            }

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = target + TempSuffix;
            try
            {
                await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
                File.Move(temp, target, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch { /* best-effort */ }
                throw;
            }

            return new WriteRepositoryFileResponse { Success = true, Written = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WriteRepositoryFileResponse { ErrorMessage = ex.Message };
        }
    }
}
