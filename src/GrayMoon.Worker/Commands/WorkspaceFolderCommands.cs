using System.Text;
using GrayMoon.Common.Manifest;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>Reports the folders the Worker holds file watchers on. Reads only.</summary>
public sealed class GetGitChangesMonitoringCommand(IGitChangesMonitoring monitoring)
    : ICommandHandler<GetGitChangesMonitoringRequest, GetGitChangesMonitoringResponse>
{
    public Task<GetGitChangesMonitoringResponse> ExecuteAsync(GetGitChangesMonitoringRequest request, CancellationToken cancellationToken = default)
    {
        var response = new GetGitChangesMonitoringResponse();
        response.Repositories.AddRange(monitoring.GetMonitored(request.WorkspaceId).Select(m => new MonitoredRepositoryInfo
        {
            Path = m.Path,
            WorkspaceId = m.WorkspaceId,
            RepositoryId = m.RepositoryId,
            IsHealthy = m.IsHealthy,
            LeaseCount = m.LeaseCount,
            ExpiresAt = m.ExpiresAt,
        }));
        return Task.FromResult(response);
    }
}

/// <summary>Lists the folders directly under the workspace root. Reads only; never looks inside them.</summary>
public sealed class ListWorkspaceRootFoldersCommand()
    : ICommandHandler<ListWorkspaceRootFoldersRequest, ListWorkspaceRootFoldersResponse>
{
    public Task<ListWorkspaceRootFoldersResponse> ExecuteAsync(ListWorkspaceRootFoldersRequest request, CancellationToken cancellationToken = default)
    {
        var root = request.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("workspaceRoot required");

        var response = new ListWorkspaceRootFoldersResponse();
        response.Folders.AddRange(WorkerRepositoryPaths.GetDirectoryNames(root).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        return Task.FromResult(response);
    }
}

/// <summary>
/// Looks at one folder under the workspace root for Import from disk: whether it is a Git repository, which
/// sub-folders are, their origin URLs, and the text of its Workspace definition file. Writes nothing.
/// </summary>
public sealed class InspectWorkspaceFolderCommand(IGitRepositoryReader reader)
    : ICommandHandler<InspectWorkspaceFolderRequest, InspectWorkspaceFolderResponse>
{
    private const int DefaultMaxConcurrentRepos = 8;

    /// <summary>Largest definition file that is read. A real one is a few kilobytes.</summary>
    internal const int MaxDefinitionBytes = 1024 * 1024;

    public async Task<InspectWorkspaceFolderResponse> ExecuteAsync(InspectWorkspaceFolderRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        if (WorkerRepositoryPaths.GetRepositoryNameRejection(workspaceName) is { } rejection)
            throw new ArgumentException(rejection.Replace("Repository name", "Folder name"));

        var path = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var response = new InspectWorkspaceFolderResponse { Exists = Directory.Exists(path) };
        if (!response.Exists)
            return response;

        if (WorkerRepositoryPaths.HasGitMetadata(path))
        {
            response.RootRepository = new WorkspaceRepositoryInfo
            {
                Name = workspaceName,
                OriginUrl = await reader.GetRemoteOriginUrlAsync(path, cancellationToken),
            };
        }

        var names = WorkerRepositoryPaths.GetDirectoryNames(path)
            .Where(name => WorkerRepositoryPaths.HasGitMetadata(Path.Combine(path, name)))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var maxConcurrent = request.MaxParallelOperations is > 0 ? request.MaxParallelOperations.Value : DefaultMaxConcurrentRepos;
        using var semaphore = new SemaphoreSlim(maxConcurrent);
        response.Repositories.AddRange(await Task.WhenAll(names.Select(async name =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return new WorkspaceRepositoryInfo
                {
                    Name = name,
                    OriginUrl = await reader.GetRemoteOriginUrlAsync(Path.Combine(path, name), cancellationToken),
                };
            }
            finally
            {
                semaphore.Release();
            }
        })));

        var definitionPath = Path.Combine(path, WorkspaceManifestFile.FileName);
        try
        {
            var info = new FileInfo(definitionPath);
            if (info.Exists)
            {
                response.DefinitionFound = true;
                if (info.Length > MaxDefinitionBytes)
                    response.DefinitionError = $"{WorkspaceManifestFile.FileName} is larger than {MaxDefinitionBytes / 1024} KB and was not read.";
                else
                    response.DefinitionContent = await File.ReadAllTextAsync(definitionPath, Encoding.UTF8, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            response.DefinitionFound = true;
            response.DefinitionError = $"{WorkspaceManifestFile.FileName} could not be read: {ex.Message}";
        }

        return response;
    }
}
