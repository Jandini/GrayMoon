using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Services;

public sealed class GitService : IGitService
{
    private readonly string _workspaceRoot;
    private readonly int _listenPort;
    private readonly ILogger<GitService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public GitService(IOptions<AgentOptions> options, ILogger<GitService> logger)
    {
        _logger = logger;
        var root = options.Value.WorkspaceRoot;
        _workspaceRoot = string.IsNullOrWhiteSpace(root)
            ? (OperatingSystem.IsWindows() ? @"C:\Workspace" : "/var/graymoon/workspaces")
            : root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _listenPort = options.Value.ListenPort;
    }

    public string WorkspaceRoot => _workspaceRoot;

    public string GetWorkspacePath(string workspaceName)
    {
        var safe = SanitizeDirectoryName(workspaceName);
        return Path.Combine(_workspaceRoot, safe);
    }

    public async Task<bool> CloneAsync(string workingDir, string cloneUrl, string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDir))
            throw new ArgumentException("Working directory is required.", nameof(workingDir));
        if (string.IsNullOrWhiteSpace(cloneUrl))
            throw new ArgumentException("Clone URL is required.", nameof(cloneUrl));

        if (!Directory.Exists(workingDir))
            Directory.CreateDirectory(workingDir);

        var args = BuildCloneArguments(cloneUrl, bearerToken);
        var (exitCode, _, stderr) = await RunProcessAsync("git", args, workingDir, ct);
        if (exitCode != 0)
        {
            _logger.LogWarning("Git clone failed. ExitCode={ExitCode}, Stderr={Stderr}", exitCode, stderr);
            return false;
        }
        _logger.LogInformation("Git clone completed: {Url} -> {Dir}", cloneUrl, workingDir);
        return true;
    }

    public async Task AddSafeDirectoryAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return;

        var fullPath = Path.GetFullPath(repoPath);
        var args = $"config --global --add safe.directory \"{fullPath.Replace("\"", "\\\"")}\"";
        await RunProcessAsync("git", args, null, ct);
    }

    public async Task<GitVersionResult?> GetVersionAsync(string repoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return null;

        var (exitCode, stdout, stderr) = await RunProcessAsync("dotnet-gitversion", "", repoPath, ct);
        if (exitCode != 0)
        {
            _logger.LogWarning("dotnet-gitversion failed. ExitCode={ExitCode}, Stderr={Stderr}", exitCode, stderr);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GitVersionResult>(stdout ?? "", JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void CreateDirectory(string path)
    {
        if (Directory.Exists(path))
            return;
        Directory.CreateDirectory(path);
        _logger.LogInformation("Created directory: {Path}", path);
    }

    public bool DirectoryExists(string path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    public string[] GetDirectories(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return [];
        return Directory.GetDirectories(path).Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToArray();
    }

    public void WriteSyncHooks(string repoPath, int workspaceId, int repositoryId)
    {
        var hooksDir = Path.Combine(repoPath, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);

        var notifyUrl = $"http://127.0.0.1:{_listenPort}/notify";
        var payload = JsonSerializer.Serialize(new { repositoryId, workspaceId, repositoryPath = repoPath });
        var curlLine = $"curl -s -X POST \"{notifyUrl}\" -H \"Content-Type: application/json\" -d '{payload.Replace("'", "'\\''")}'";

        var utf8 = new UTF8Encoding(false);
        var comment = $"# Created by GrayMoon.Agent at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z\n";

        WriteHookFile(Path.Combine(hooksDir, "post-commit"), "#!/bin/sh\n" + comment + curlLine + "\n", utf8);
        WriteHookFile(Path.Combine(hooksDir, "post-checkout"), "#!/bin/sh\n" + comment + "[ \"$3\" = \"1\" ] && " + curlLine.TrimEnd() + "\n", utf8);
        _logger.LogDebug("Sync hooks written for repo {RepoId} in workspace {WorkspaceId}", repositoryId, workspaceId);
    }

    private static void WriteHookFile(string path, string content, Encoding encoding)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        File.WriteAllText(path, normalized, encoding);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string BuildCloneArguments(string cloneUrl, string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            return $"clone \"{cloneUrl}\"";
        var credentials = "x-access-token:" + bearerToken;
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        var headerValue = "Authorization: Basic " + base64;
        var escaped = headerValue.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"-c \"http.extraHeader={escaped}\" clone \"{cloneUrl}\"";
    }

    private static string SanitizeDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "workspace";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Trim().Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "workspace" : sanitized;
    }

    private static async Task<(int ExitCode, string? Stdout, string? Stderr)> RunProcessAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
            return (-1, null, "Failed to start process");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }
}
