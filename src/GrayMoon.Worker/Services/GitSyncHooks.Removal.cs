using System.Collections.Concurrent;
using System.Globalization;
using GrayMoon.Worker.Models;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services;

/// <summary>Removal of GrayMoon's sync hooks, the reverse of <see cref="InstallAsync"/>.</summary>
internal sealed partial class GitSyncHooks
{
    private const string ReplacedTimestampFormat = "yyyyMMddHHmmss";

    // One gate per hooks folder, shared by every instance in the process: install and removal take the same one.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DirectoryGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Asks git where the repository keeps its hooks. Runs git, so it must be called before an exclusive claim covers
    /// the folder. A missing folder and a folder without <c>.git</c> are settled here as successes.
    /// </summary>
    public async Task<SyncHookRemovalTarget> ResolveRemovalTargetAsync(string repoPath, CancellationToken ct)
    {
        if (!Directory.Exists(repoPath))
            return Settled(repoPath, SyncHookRemovalResult.Of(SyncHookRemovalOutcome.NotFound));
        if (!WorkerRepositoryPaths.HasGitMetadata(repoPath))
            return Settled(repoPath, SyncHookRemovalResult.Of(SyncHookRemovalOutcome.NotARepository));

        // Asked fresh, never from the cache: the answer decides which files are deleted.
        var (exit, stdout, stderr) = await runner.RunAsync(
            "git", ["rev-parse", "--git-common-dir", "--git-path", "hooks"], repoPath, null, ct, GitLockIntent.Read);
        var lines = (stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (exit != 0 || lines.Length != 2)
        {
            var message = FirstLine(stderr) ?? FirstLine(stdout) ?? $"git rev-parse failed (exit code {exit}).";
            return Settled(repoPath, SyncHookRemovalResult.Failure(SyncHookRemovalFailureKind.GitFailed, repoPath, message));
        }

        var commonDir = Path.GetFullPath(Path.Combine(repoPath, lines[0]));
        var resolvedHooksDir = Path.GetFullPath(Path.Combine(repoPath, lines[1]));

        // GrayMoon only ever writes inside the common git directory: its own hooks folder, and the folder git resolves
        // when core.hooksPath points somewhere else inside it. Hooks written before that setting changed are still in
        // the first one, so both are looked at. A folder outside the common directory is never touched.
        var directories = new List<string> { Path.Combine(commonDir, "hooks") };
        if (IsPathInside(resolvedHooksDir, commonDir)
            && !directories.Contains(resolvedHooksDir, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            directories.Add(resolvedHooksDir);
        }

        return new SyncHookRemovalTarget(repoPath, directories, null);
    }

    /// <summary>
    /// Deletes every GrayMoon hook in the target's hooks folders and renames the user's own replaced hook back. File
    /// work only: no process is started, so it may run inside an exclusive claim on the repository.
    /// </summary>
    public SyncHookRemovalResult Remove(SyncHookRemovalTarget target)
    {
        if (target.Settled is not null)
            return target.Settled;

        var removed = new List<string>();
        var restored = new List<string>();
        var foreign = new List<string>();
        var leftover = new List<string>();

        foreach (var directory in target.HookDirectories)
        {
            if (!Directory.Exists(directory))
                continue;

            var gate = DirectoryGates.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
            gate.Wait();
            try
            {
                var failure = RemoveFromDirectory(directory, removed, restored, foreign, leftover);
                if (failure is not null)
                    return failure with { RemovedHooks = removed, RestoredHooks = restored, ForeignHooks = foreign, LeftoverReplacedHooks = leftover };
            }
            finally
            {
                gate.Release();
            }
        }

        _hooksLocations.Forget(target.RepositoryPath);
        logger.LogInformation(
            "Sync hooks removed from {RepoPath}: removed=[{Removed}] restored=[{Restored}] foreign=[{Foreign}] leftoverReplaced=[{Leftover}]",
            target.RepositoryPath, string.Join(", ", removed), string.Join(", ", restored), string.Join(", ", foreign), string.Join(", ", leftover));

        return new SyncHookRemovalResult
        {
            Outcome = removed.Count > 0 ? SyncHookRemovalOutcome.Removed : SyncHookRemovalOutcome.NothingToRemove,
            RemovedHooks = removed,
            RestoredHooks = restored,
            ForeignHooks = foreign,
            LeftoverReplacedHooks = leftover,
        };
    }

    /// <summary>Resolves and removes in one call, for a caller that holds no claim on the repository.</summary>
    public async Task<SyncHookRemovalResult> RemoveAsync(string repoPath, CancellationToken ct) =>
        Remove(await ResolveRemovalTargetAsync(repoPath, ct));

    private SyncHookRemovalResult? RemoveFromDirectory(
        string directory, List<string> removed, List<string> restored, List<string> foreign, List<string> leftover)
    {
        foreach (var name in HookNames)
        {
            var path = Path.Combine(directory, name);
            try
            {
                if (Directory.Exists(path) || !File.Exists(path))
                    continue;

                if (!IsGrayMoonHook(File.ReadAllText(path)))
                {
                    foreign.Add(name);
                    continue;
                }

                File.Delete(path);
                removed.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete the GrayMoon hook {HookPath}", path);
                return SyncHookRemovalResult.Failure(SyncHookRemovalFailureKind.HookNotDeleted, path, ex.Message);
            }

            var replaced = FindReplacedHooks(directory, name);
            if (replaced.Count == 0)
                continue;

            // The newest is the hook the user last had active; older ones stay where they are and are reported.
            var newest = replaced[0];
            try
            {
                File.Move(newest, path);
                restored.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not rename the replaced hook {ReplacedPath} back to {HookPath}", newest, path);
                return SyncHookRemovalResult.Failure(SyncHookRemovalFailureKind.HookNotRestored, newest, ex.Message);
            }

            leftover.AddRange(replaced.Skip(1).Select(p => Path.GetFileName(p)!));
        }

        // Nothing of GrayMoon's may be left under a hook name.
        foreach (var name in HookNames)
        {
            var path = Path.Combine(directory, name);
            try
            {
                if (File.Exists(path) && IsGrayMoonHook(File.ReadAllText(path)))
                    return SyncHookRemovalResult.Failure(SyncHookRemovalFailureKind.HookNotDeleted, path, "The hook file is still there after it was deleted.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return SyncHookRemovalResult.Failure(SyncHookRemovalFailureKind.HookNotDeleted, path, ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// The files install moved aside for <paramref name="hookName"/>, newest first: those with a timestamp in their
    /// name by that timestamp, then the one without (it is only ever created when no replaced file existed yet).
    /// </summary>
    internal static List<string> FindReplacedHooks(string directory, string hookName)
    {
        var plain = hookName + ReplacedHookSuffix;
        var prefix = plain + "-";
        var timestamped = new List<(DateTime Stamp, string Path)>();
        string? plainPath = null;

        foreach (var file in Directory.EnumerateFiles(directory, plain + "*"))
        {
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, plain, StringComparison.Ordinal))
            {
                plainPath = file;
            }
            else if (fileName.StartsWith(prefix, StringComparison.Ordinal)
                     && DateTime.TryParseExact(fileName[prefix.Length..], ReplacedTimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
            {
                timestamped.Add((stamp, file));
            }
        }

        var ordered = timestamped.OrderByDescending(t => t.Stamp).Select(t => t.Path).ToList();
        if (plainPath is not null)
            ordered.Add(plainPath);
        return ordered;
    }

    private static async Task<IDisposable> EnterDirectoryAsync(string directory, CancellationToken ct)
    {
        var gate = DirectoryGates.GetOrAdd(Path.GetFullPath(directory), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new GateRelease(gate);
    }

    private static SyncHookRemovalTarget Settled(string repoPath, SyncHookRemovalResult result) => new(repoPath, [], result);

    private static string? FirstLine(string? text)
    {
        var line = text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    private sealed class GateRelease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
