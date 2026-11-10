using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Common;
using GrayMoon.Common.Git;
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

            // Every project is attempted; the response fails when any of them did, and names each failure.
            var failures = new List<string>();
            foreach (var relativePath in projectPaths)
            {
                if (string.IsNullOrWhiteSpace(relativePath))
                    continue;

                // The App is not a trusted source of paths: a rooted or ".." project path is never handed to dotnet.
                var validation = GitRepositoryPathValidator.Validate(repoPath, relativePath);
                if (!validation.IsValid || validation.FullPath is null)
                {
                    logger.LogWarning("dotnet restore rejected {ProjectPath}: {Reason}", relativePath, validation.ErrorMessage);
                    failures.Add($"{relativePath}: {validation.ErrorMessage ?? "Invalid path."}");
                    continue;
                }

                try
                {
                    // An argument list, never a command string: a quote or a space in the path cannot change the tokens.
                    var result = await commandLine.RunAsync(
                        "dotnet", (IReadOnlyList<string>)["restore", "--force", "--no-cache", validation.FullPath],
                        repoPath, null, cancellationToken,
                        streamStderrAsStdout: true);

                    if (result.ExitCode != 0)
                    {
                        logger.LogWarning("dotnet restore exited {ExitCode} for {ProjectPath}: {Stderr}",
                            result.ExitCode, relativePath, result.Stderr);
                        failures.Add($"{relativePath}: {DescribeFailure(result)}");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "dotnet restore failed for {ProjectPath}", relativePath);
                    failures.Add($"{relativePath}: {ex.Message}");
                }
            }

            return failures.Count == 0
                ? new DotnetRestoreResponse { Success = true }
                : new DotnetRestoreResponse { Success = false, ErrorMessage = string.Join(Environment.NewLine, failures) };
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

    private const int MaxFailureTailLength = 600;

    /// <summary>
    /// The end of what dotnet printed for a failed restore. dotnet writes its errors (NU1101 and the like) to
    /// stdout, so stdout is used when stderr is empty. A run the command line service killed on its timeout
    /// (exit code -1, stderr describing it) is reported as that timeout.
    /// </summary>
    internal static string DescribeFailure(CommandLineResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.Stderr) ? result.Stderr : result.Stdout;
        var tail = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (tail is { Length: > MaxFailureTailLength })
            tail = "..." + tail[^MaxFailureTailLength..];
        if (result.ExitCode == -1)
            return tail ?? "dotnet restore timed out or could not be started.";
        return tail is null
            ? $"dotnet restore failed (exit code {result.ExitCode})."
            : $"dotnet restore failed (exit code {result.ExitCode}): {tail}";
    }
}
