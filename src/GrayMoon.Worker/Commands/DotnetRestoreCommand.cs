using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Common;
using Microsoft.Extensions.Logging;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

public sealed class DotnetRestoreCommand(ICommandLineService commandLine, ILogger<DotnetRestoreCommand> logger)
    : ICommandHandler<DotnetRestoreRequest, DotnetRestoreResponse>
{
    public async Task<DotnetRestoreResponse> ExecuteAsync(DotnetRestoreRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
            var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");

            var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
            var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);

            if (!Directory.Exists(repoPath))
                return new DotnetRestoreResponse { Success = true };

            var projectPaths = request.ProjectPaths;
            if (projectPaths == null || projectPaths.Count == 0)
                return new DotnetRestoreResponse { Success = true };

            foreach (var relativePath in projectPaths)
            {
                if (string.IsNullOrWhiteSpace(relativePath))
                    continue;

                var fullPath = Path.Combine(repoPath, relativePath);
                try
                {
                    var result = await commandLine.RunAsync(
                        "dotnet", $"restore --force --no-cache \"{fullPath}\"",
                        repoPath, null, cancellationToken,
                        streamStderrAsStdout: true);

                    if (result.ExitCode != 0)
                        logger.LogWarning("dotnet restore exited {ExitCode} for {ProjectPath}: {Stderr}",
                            result.ExitCode, relativePath, result.Stderr);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "dotnet restore failed for {ProjectPath}", relativePath);
                }
            }

            return new DotnetRestoreResponse { Success = true };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "dotnet restore failed for {RepoName}", request.RepositoryName);
            return new DotnetRestoreResponse { Success = false, ErrorMessage = ex.Message };
        }
    }
}
