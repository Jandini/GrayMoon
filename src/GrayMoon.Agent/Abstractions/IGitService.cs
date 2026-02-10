using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Abstractions;

public interface IGitService
{
    string WorkspaceRoot { get; }
    string GetWorkspacePath(string workspaceName);
    Task<bool> CloneAsync(string workingDir, string cloneUrl, string? bearerToken, CancellationToken ct);
    Task AddSafeDirectoryAsync(string repoPath, CancellationToken ct);
    Task<GitVersionResult?> GetVersionAsync(string repoPath, CancellationToken ct);
    Task<string?> GetRemoteOriginUrlAsync(string repoPath, CancellationToken ct);
    void CreateDirectory(string path);
    bool DirectoryExists(string path);
    string[] GetDirectories(string path);
    void WriteSyncHooks(string repoPath, int workspaceId, int repositoryId);
}
