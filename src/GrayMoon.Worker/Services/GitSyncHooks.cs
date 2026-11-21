using System.Text;
using GrayMoon.Worker.Abstractions;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services;

/// <summary>
/// The single owner of GrayMoon's sync hooks in a repository: the marker text, the hook names, the scripts, where they
/// are written (only inside the common Git directory), installing them and removing them again. Nothing else in the
/// Worker writes or deletes a hook file.
/// </summary>
internal sealed partial class GitSyncHooks(int listenPort, ILogger logger, GitProcessRunner runner, IRepositoryAccess? access)
{
    /// <summary>The hook files GrayMoon installs, in install order.</summary>
    internal static readonly string[] HookNames =
        ["post-commit", "post-checkout", "post-merge", "pre-push", "reference-transaction"];
    // Persisted in users' .git/hooks files since before the Agent -> Worker rename. Do not change the text:
    // existing hooks are recognized (and replaced/removed) by this exact prefix.
    internal const string Marker = "# Created by GrayMoon.Agent";
    internal const string ReplacedHookSuffix = ".replaced-by-graymoon";

    // reference-transaction runs twice for every ref update, and every commit moves a branch, so the script filters with
    // shell builtins only and starts git and curl once a branch or remote-branch ref was really created or deleted
    // (one side of the update is the all-zero object id, 40 or 64 zeros). It acts on "committed" only: "prepared" and
    // "aborted" mean the refs did not change, and exiting non-zero in "prepared" would abort the user's own command.
    // Updates of existing branches, HEAD, tags, the stash and refs/remotes/*/HEAD never match. A blind update (for
    // example "git update-ref <branch> <new>" without an old value) also shows a zero old id and so reports too; that
    // costs one idempotent re-listing of the branches, never a wrong result.
    private const string ReferenceTransactionFilter =
        "[ \"$1\" = \"committed\" ] || exit 0\n" +
        "MATCH=\n" +
        "while read -r OLD NEW REF; do\n" +
        "  case \"$REF\" in\n" +
        "    refs/remotes/*/HEAD) continue ;;\n" +
        "    refs/heads/*|refs/remotes/*) ;;\n" +
        "    *) continue ;;\n" +
        "  esac\n" +
        "  [ -n \"$OLD\" ] && [ -n \"$NEW\" ] || continue\n" +
        "  case \"$OLD\" in *[!0]*) ;; *) MATCH=1 ;; esac\n" +
        "  case \"$NEW\" in *[!0]*) ;; *) MATCH=1 ;; esac\n" +
        "done\n" +
        "[ -n \"$MATCH\" ] || exit 0\n";

    // Hooks locations git has already resolved, replayed while the repository layout and config are unchanged.
    private readonly GitHooksLocationCache _hooksLocations = new();

    public async Task InstallAsync(string repoPath, int workspaceId, int repositoryId, CancellationToken ct)
    {
        try
        {
            await InstallCoreAsync(repoPath, workspaceId, repositoryId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not install sync hooks for {RepoPath} (workspace {WorkspaceId}, repo {RepositoryId}); live updates for changes made outside GrayMoon will appear only after the next Sync.",
                repoPath, workspaceId, repositoryId);
        }
    }

    private async Task InstallCoreAsync(string repoPath, int workspaceId, int repositoryId, CancellationToken ct)
    {
        var (location, locationSource) = await _hooksLocations.ResolveAsync(repoPath, () => ResolveGitHooksLocationAsync(repoPath, ct));
        logger.LogDebug("Hooks location for {RepoPath}: {Source}", repoPath, locationSource);
        if (location.Directory is null)
        {
            logger.LogWarning(
                "Could not resolve git hooks directory for {RepoPath} (workspace {WorkspaceId}, repo {RepositoryId}); skipping hook install.",
                repoPath, workspaceId, repositoryId);
            return;
        }

        if (location.OutsideGitDirHooksPath is not null)
        {
            logger.LogWarning(
                "Repository {RepoPath} sets core.hooksPath to '{HooksPath}', outside its Git directory. GrayMoon did not write or change any Git hooks there, so live updates for changes made outside GrayMoon will appear only after the next Sync.",
                repoPath, location.OutsideGitDirHooksPath);
            return;
        }

        var hooksDir = location.Directory;
        Directory.CreateDirectory(hooksDir);

        // Install and removal of one hooks folder never interleave.
        using var directoryLock = await EnterDirectoryAsync(hooksDir, ct);

        // Context-agnostic hooks: resolve the executing worktree root at runtime so linked
        // Feature worktrees attribute correctly. Do not embed a static Feature context id.
        var utf8 = new UTF8Encoding(false);
        var comment = $"{Marker} at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z\n";
        var resolveBody =
            "REPO_PATH=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0\n" +
            "REPO_JSON=$(printf '%s' \"$REPO_PATH\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g')\n" +
            $"PAYLOAD=\"{{\\\"repositoryId\\\":{repositoryId},\\\"workspaceId\\\":{workspaceId},\\\"repositoryPath\\\":\\\"$REPO_JSON\\\"}}\"\n";
        var header = "-H \"Content-Type: application/json\"";
        var curlFlags = "-s --connect-timeout 1 --max-time 2";

        string Curl(string hookPath) =>
            $"curl {curlFlags} -X POST \"http://127.0.0.1:{listenPort}/hook/{hookPath}\" {header} -d \"$PAYLOAD\" || true";

        InstallSyncHook(repoPath, hooksDir, "post-commit",
            "#!/bin/sh\n" + comment + resolveBody + Curl("commit") + "\n", utf8);
        InstallSyncHook(repoPath, hooksDir, "post-checkout",
            "#!/bin/sh\n" + comment + "[ \"$3\" = \"1\" ] || exit 0\n" + resolveBody + Curl("checkout") + "\n", utf8);
        InstallSyncHook(repoPath, hooksDir, "post-merge",
            "#!/bin/sh\n" + comment + resolveBody + Curl("merge") + "\n", utf8);
        InstallSyncHook(repoPath, hooksDir, "pre-push",
            "#!/bin/sh\n" + comment + resolveBody + Curl("push") + "\n", utf8);
        // Git older than 2.28 never runs this hook, so installing it there is harmless.
        InstallSyncHook(repoPath, hooksDir, "reference-transaction",
            "#!/bin/sh\n" + comment + ReferenceTransactionFilter + resolveBody + Curl("refs") + "\n", utf8);
        logger.LogDebug("Sync hooks checked for repo {RepoId} in workspace {WorkspaceId}", repositoryId, workspaceId);
    }

    private void InstallSyncHook(string repoPath, string hooksDir, string hookName, string content, Encoding encoding)
    {
        var path = Path.Combine(hooksDir, hookName);
        try
        {
            if (Directory.Exists(path))
            {
                logger.LogWarning(
                    "Cannot install the GrayMoon {Hook} hook for {RepoPath}: '{HookPath}' is a directory. Live updates for that Git event will appear only after the next Sync.",
                    hookName, repoPath, path);
                return;
            }

            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path);
                if (IsGrayMoonHook(existing))
                {
                    if (WithoutMarkerLine(existing) == WithoutMarkerLine(content))
                        return;
                }
                else
                {
                    string replacedPath;
                    try
                    {
                        replacedPath = MoveForeignHookAside(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(ex,
                            "Could not rename the existing Git hook '{Hook}' in {RepoPath}. GrayMoon left it untouched and did not install its own {Hook} hook, so live updates for that Git event will appear only after the next Sync.",
                            hookName, repoPath, hookName);
                        return;
                    }

                    logger.LogWarning(
                        "Repository {RepoPath} already had its own Git hook '{Hook}'. GrayMoon renamed it to '{ReplacedName}' and installed its own hook. The renamed hook no longer runs (Git only runs files named exactly like the hook); rename it back to restore it.",
                        repoPath, hookName, Path.GetFileName(replacedPath));
                }
            }

            WriteHookFile(path, content, encoding);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex,
                "Could not install the GrayMoon {Hook} hook for {RepoPath}; live updates for that Git event will appear only after the next Sync.",
                hookName, repoPath);
        }
    }

    private static string MoveForeignHookAside(string path)
    {
        var replacedPath = path + ReplacedHookSuffix;
        if (File.Exists(replacedPath) || Directory.Exists(replacedPath))
            replacedPath = $"{path}{ReplacedHookSuffix}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Move(path, replacedPath);
        return replacedPath;
    }

    internal static bool IsGrayMoonHook(string content)
    {
        using var reader = new StringReader(content);
        var line = reader.ReadLine();
        if (line is not null && line.StartsWith("#!", StringComparison.Ordinal))
            line = reader.ReadLine();
        while (line is not null && string.IsNullOrWhiteSpace(line))
            line = reader.ReadLine();
        return line is not null && line.StartsWith(Marker, StringComparison.Ordinal);
    }

    private static string WithoutMarkerLine(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        var markerIndex = lines.FindIndex(l => l.StartsWith(Marker, StringComparison.Ordinal));
        if (markerIndex >= 0)
            lines.RemoveAt(markerIndex);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Resolves the hooks directory that Git will actually execute for <paramref name="repoPath"/> via
    /// <c>git rev-parse --git-path hooks</c>, which honours <c>core.hooksPath</c>. For a linked worktree
    /// (a GrayMoon Feature or an external worktree) this is the common Git directory's <c>hooks</c> folder,
    /// shared by every linked worktree (see design doc &#167;13.1). When <c>core.hooksPath</c> is set and
    /// resolves outside the common Git directory (husky, lefthook, a shared folder), the configured value
    /// is returned in <see cref="GitHooksLocation.OutsideGitDirHooksPath"/> and nothing may be written there.
    /// </summary>
    private async Task<GitHooksLocation> ResolveGitHooksLocationAsync(string repoPath, CancellationToken ct)
    {
        // One call answers both questions: where git will look for hooks (which honours core.hooksPath) and where
        // the common git directory is. Output is one line per argument, in argument order. A hooks folder inside
        // the common directory is where we may write, whether or not core.hooksPath is set, so the config only
        // has to be read in the other case - to name the setting that sent hooks elsewhere.
        var (exit, stdout, _) = await runner.RunAsync(
            "git", ["rev-parse", "--git-common-dir", "--git-path", "hooks"], repoPath, null, ct, GitLockIntent.Read);
        if (exit != 0)
            return new GitHooksLocation(null, null);

        var lines = (stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 2 || string.IsNullOrWhiteSpace(lines[0]) || string.IsNullOrWhiteSpace(lines[1]))
            return new GitHooksLocation(null, null);

        var commonDir = Path.GetFullPath(Path.Combine(repoPath, lines[0]));
        var hooksDir = Path.GetFullPath(Path.Combine(repoPath, lines[1]));
        if (IsPathInside(hooksDir, commonDir))
            return new GitHooksLocation(hooksDir, null);

        // Only this read of the setting's text (to name it in the warning) is in-process. Where is it not shown to
        // match native git (environment-redirected config, per-worktree config, a repository libgit2 cannot open),
        // the original "git config --get" runs. Which directory git executes hooks from is always decided above.
        string? configured;
        var inProcess = LibGit2SharpConfigReader.TryGetString(repoPath, "core.hooksPath", access, ct);
        if (inProcess.Handled)
        {
            configured = inProcess.Value?.Trim();
        }
        else
        {
            var (configExit, configOut, _) = await runner.RunAsync(
                "git", ["config", "--get", "core.hooksPath"], repoPath, null, ct, GitLockIntent.Read);
            configured = configExit == 0 ? configOut?.Trim() : null;
        }

        return string.IsNullOrEmpty(configured)
            ? new GitHooksLocation(hooksDir, null)
            : new GitHooksLocation(hooksDir, configured);
    }

    private static bool IsPathInside(string path, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var separator = Path.DirectorySeparatorChar;
        var normalizedParent = Path.TrimEndingDirectorySeparator(parent) + separator;
        var normalizedPath = Path.TrimEndingDirectorySeparator(path) + separator;
        return normalizedPath.StartsWith(normalizedParent, comparison);
    }

    private static void WriteHookFile(string path, string content, Encoding encoding)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        File.WriteAllText(path, normalized, encoding);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
