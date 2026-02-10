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
    /// <summary>Fetches from origin; when <paramref name="includeTags"/> is true, fetches tags as well.</summary>
    Task FetchAsync(string repoPath, bool includeTags, string? bearerToken, CancellationToken ct);
    /// <summary>Returns (outgoing count, incoming count) for the current branch vs origin/branchName. Returns (null, null) if unavailable.</summary>
    Task<(int? Outgoing, int? Incoming)> GetCommitCountsAsync(string repoPath, string branchName, CancellationToken ct);
    void CreateDirectory(string path);
    bool DirectoryExists(string path);
    string[] GetDirectories(string path);
    void WriteSyncHooks(string repoPath, int workspaceId, int repositoryId);
}
