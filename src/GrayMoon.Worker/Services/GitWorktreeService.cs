using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;
using Microsoft.Extensions.Logging;
using static GrayMoon.Worker.Services.GitCliOutput;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Feature worktree lifecycle (list / create / remove / inspect) over native <c>git worktree</c>, including the
/// residue-cleanup safety guards. Local repository facts come from <see cref="IGitRepositoryReader"/>;
/// worktree add/remove stay on the git CLI.
/// </summary>
public sealed class GitWorktreeService(GitProcessRunner runner, IGitRepositoryReader reader, ILogger<GitWorktreeService> logger) : IGitWorktreeService
{
    private static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
    private static readonly int[] ResidueDeleteRetryDelaysMs = [200, 400, 800, 1600, 3200];

    public async Task<(bool Success, IReadOnlyList<GitWorktreeInfo> Worktrees, string? ErrorCode, string? ErrorMessage)> ListWorktreesAsync(
        string mainRepositoryPath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mainRepositoryPath) || !Directory.Exists(mainRepositoryPath))
            return (false, [], "RepositoryNotFound", "Repository not found.");

        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            ["worktree", "list", "--porcelain"],
            mainRepositoryPath,
            null,
            ct,
            GitLockIntent.Read);

        if (exitCode != 0)
        {
            var error = CombineOutput(stdout, stderr) ?? "git worktree list failed";
            logger.LogError("Git worktree list failed for {RepoPath}. ExitCode={ExitCode}", mainRepositoryPath, exitCode);
            return (false, [], "GitFailed", error);
        }

        return (true, GitWorktreePorcelainParser.Parse(stdout), null, null);
    }

    public async Task<(bool Success, GitWorktreeInfo? Worktree, bool AlreadyExisted, string? ErrorCode, string? ErrorMessage)> CreateWorktreeAsync(
        string mainRepositoryPath,
        string worktreePath,
        string? branchName,
        string baseCommitSha,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mainRepositoryPath) || !Directory.Exists(mainRepositoryPath))
            return (false, null, false, "RepositoryNotFound", "Repository not found.");
        if (string.IsNullOrWhiteSpace(worktreePath))
            return (false, null, false, "InvalidWorktreePath", "worktreePath is required.");
        var detach = string.IsNullOrWhiteSpace(branchName);
        if (string.IsNullOrWhiteSpace(baseCommitSha))
            return (false, null, false, "InvalidBaseCommit", "baseCommitSha is required.");

        string canonicalWorktreePath;
        try
        {
            canonicalWorktreePath = Path.GetFullPath(worktreePath);
        }
        catch (Exception ex)
        {
            return (false, null, false, "InvalidWorktreePath", ex.Message);
        }

        var (listOk, worktrees, listCode, listError) = await ListWorktreesAsync(mainRepositoryPath, ct);
        if (!listOk)
            return (false, null, false, listCode, listError);

        var existingAtPath = GitWorktreeOccupancy.FindByPath(worktrees, canonicalWorktreePath);
        if (existingAtPath != null)
        {
            var matches = detach
                ? existingAtPath.IsDetached
                  && string.Equals(existingAtPath.HeadSha, baseCommitSha, StringComparison.OrdinalIgnoreCase)
                : GitWorktreeOccupancy.MatchesExpected(existingAtPath, branchName);
            if (matches)
            {
                logger.LogInformation(
                    "Worktree already exists at {WorktreePath} on {Branch}; treating create as idempotent success.",
                    canonicalWorktreePath, branchName ?? "(detached)");
                return (true, existingAtPath, true, null, null);
            }

            return (false, existingAtPath, false, "WorktreePathConflict",
                $"Path already hosts a worktree on branch '{existingAtPath.BranchName ?? "(detached)"}'.");
        }

        var branchOccupied = detach ? null : GitWorktreeOccupancy.FindByBranch(worktrees, branchName);
        if (branchOccupied != null)
        {
            return (false, branchOccupied, false, "BranchOccupied",
                $"Branch '{branchName}' is already checked out at '{branchOccupied.WorktreePath}'.");
        }

        if (File.Exists(canonicalWorktreePath))
        {
            return (false, null, false, "PathExists",
                $"Worktree path already exists on disk: {canonicalWorktreePath}");
        }

        // An existing, empty folder is allowed (D1): residue cleanup can legitimately leave an empty
        // worktree folder behind, and git worktree add works fine with an empty target directory.
        if (Directory.Exists(canonicalWorktreePath) && Directory.EnumerateFileSystemEntries(canonicalWorktreePath).Any())
        {
            return (false, null, false, "PathExists",
                $"Worktree path already exists on disk: {canonicalWorktreePath}");
        }

        var parent = Path.GetDirectoryName(canonicalWorktreePath);
        if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
            logger.LogInformation("Created worktree parent directory: {Path}", parent);
        }

        // Feature worktrees live deeper than Workspace checkouts; allow paths over 260 characters on Windows.
        await EnsureLongPathsAsync(mainRepositoryPath, ct);

        // Offline-safe: start from local commit SHA; never --force for normal creation.
        string[] addArgs = detach
            ? ["worktree", "add", "--detach", canonicalWorktreePath, baseCommitSha]
            : ["worktree", "add", "-b", branchName!, canonicalWorktreePath, baseCommitSha];
        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            addArgs,
            mainRepositoryPath,
            null,
            ct);

        if (exitCode != 0)
        {
            var error = CombineOutput(stdout, stderr) ?? "git worktree add failed";
            logger.LogError(
                "Git worktree add failed for {RepoPath}. Branch={Branch}, Path={WorktreePath}, ExitCode={ExitCode}",
                mainRepositoryPath, branchName, canonicalWorktreePath, exitCode);
            return (false, null, false, "GitFailed", error);
        }

        var (verifyOk, after, verifyCode, verifyError) = await ListWorktreesAsync(mainRepositoryPath, ct);
        if (!verifyOk)
            return (false, null, false, verifyCode, verifyError);

        var created = GitWorktreeOccupancy.FindByPath(after, canonicalWorktreePath)
            ?? GitWorktreeOccupancy.FindByBranch(after, branchName);
        if (created == null)
        {
            return (false, null, false, "VerifyFailed",
                "Worktree was created but could not be found in git worktree list.");
        }

        logger.LogInformation(
            "Git worktree created for {RepoPath}. Branch={Branch}, Path={WorktreePath}, Head={Head}",
            mainRepositoryPath, created.BranchName, created.WorktreePath, created.HeadSha);
        return (true, created, false, null, null);
    }

    /// <summary>
    /// On Windows, makes sure <c>core.longpaths</c> is <c>true</c> for the repository (which every linked
    /// worktree shares), so Feature worktrees with deep trees (for example <c>node_modules</c>) can be
    /// checked out, used and removed past the 260-character limit. The value is written to the repository's
    /// own config (not global, not per-worktree) and only when it is not already <c>true</c> there, so it is
    /// written at most once. Does nothing on other operating systems. Never throws: a failure is logged and
    /// the caller carries on.
    /// </summary>
    internal async Task EnsureLongPathsAsync(string repositoryPath, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var (getExit, getOut, _) = await runner.RunAsync(
                "git", ["config", "--local", "--get", "core.longpaths"], repositoryPath, null, ct, GitLockIntent.Read);
            if (getExit == 0 && string.Equals(getOut?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                return;

            var (setExit, setOut, setErr) = await runner.RunAsync(
                "git", ["config", "--local", "core.longpaths", "true"], repositoryPath, null, ct);
            if (setExit != 0)
            {
                logger.LogWarning(
                    "Could not set core.longpaths for {RepoPath}; very long paths in Feature worktrees may fail. {Error}",
                    repositoryPath, CombineOutput(setOut, setErr));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not set core.longpaths for {RepoPath}", repositoryPath);
        }
    }

    public async Task<(bool Success, bool AlreadyRemoved, string? ErrorCode, string? ErrorMessage, WorktreeResidueResult Residue)> RemoveWorktreeAsync(
        string mainRepositoryPath,
        string worktreePath,
        bool force,
        CancellationToken ct,
        string? featureRootPath = null,
        string? featureStorageRoot = null,
        bool unlock = false)
    {
        if (string.IsNullOrWhiteSpace(mainRepositoryPath) || !Directory.Exists(mainRepositoryPath))
            return (false, false, "RepositoryNotFound", "Repository not found.", WorktreeResidueResult.None);
        if (string.IsNullOrWhiteSpace(worktreePath))
            return (false, false, "InvalidWorktreePath", "worktreePath is required.", WorktreeResidueResult.None);

        string canonicalWorktreePath;
        try
        {
            canonicalWorktreePath = Path.GetFullPath(worktreePath);
        }
        catch (Exception ex)
        {
            return (false, false, "InvalidWorktreePath", ex.Message, WorktreeResidueResult.None);
        }

        var (listOk, worktrees, listCode, listError) = await ListWorktreesAsync(mainRepositoryPath, ct);
        if (!listOk)
            return (false, false, listCode, listError, WorktreeResidueResult.None);

        var existing = GitWorktreeOccupancy.FindByPath(worktrees, canonicalWorktreePath);
        if (existing == null)
        {
            logger.LogInformation("Worktree {WorktreePath} already absent from inventory; treating remove as success.", canonicalWorktreePath);
            var residueWhenAlreadyGone = await RemoveWorktreeResidueAsync(
                mainRepositoryPath, canonicalWorktreePath, featureRootPath, featureStorageRoot, isRegisteredWorktree: false, ct);
            return (true, true, null, null, residueWhenAlreadyGone);
        }

        // Never remove the main (first / primary) worktree via this primitive.
        var primary = worktrees.FirstOrDefault(w => !w.IsBare && !string.IsNullOrWhiteSpace(w.WorktreePath));
        if (primary != null && GitWorktreeOccupancy.PathsEqual(primary.WorktreePath, canonicalWorktreePath))
        {
            return (false, false, "CannotRemovePrimary", "Cannot remove the primary repository worktree.", WorktreeResidueResult.None);
        }

        // D5: unlock is only authorized after explicit consent, surfaced in the Remove dialog when
        // InspectWorktree reports the worktree as locked. A failed unlock (for example it was not
        // actually locked) is logged and the remove below is attempted anyway, so git reports the
        // real reason for any remaining failure.
        if (unlock)
        {
            var (unlockExitCode, unlockStdout, unlockStderr) = await runner.RunAsync(
                "git", new[] { "worktree", "unlock", canonicalWorktreePath }, mainRepositoryPath, null, ct);
            if (unlockExitCode != 0)
            {
                logger.LogWarning(
                    "git worktree unlock failed for {WorktreePath}: {Error}",
                    canonicalWorktreePath, CombineOutput(unlockStdout, unlockStderr));
            }
        }

        // Also covers Features created before long-path support was added.
        await EnsureLongPathsAsync(mainRepositoryPath, ct);

        var args = force
            ? new[] { "worktree", "remove", "--force", canonicalWorktreePath }
            : new[] { "worktree", "remove", canonicalWorktreePath };

        var (exitCode, stdout, stderr) = await runner.RunAsync("git", args, mainRepositoryPath, null, ct);
        var gitError = exitCode != 0 ? CombineOutput(stdout, stderr) ?? "git worktree remove failed" : null;

        var (verifyOk, after, verifyCode, verifyError) = await ListWorktreesAsync(mainRepositoryPath, ct);
        if (!verifyOk)
        {
            if (gitError != null)
                return (false, false, "GitFailed", gitError, WorktreeResidueResult.None);
            return (false, false, verifyCode, verifyError, WorktreeResidueResult.None);
        }

        if (GitWorktreeOccupancy.FindByPath(after, canonicalWorktreePath) != null)
        {
            // Still registered: git genuinely refused (for example dirty without force).
            logger.LogError(
                "Git worktree remove failed for {RepoPath}. Path={WorktreePath}, Force={Force}, ExitCode={ExitCode}",
                mainRepositoryPath, canonicalWorktreePath, force, exitCode);
            return gitError != null
                ? (false, false, "GitFailed", gitError, WorktreeResidueResult.None)
                : (false, false, "VerifyFailed", "Worktree remove reported success but path is still listed.", WorktreeResidueResult.None);
        }

        // Git unregistered the worktree either way. On Windows, deleting the directory itself can fail
        // (for example a file still open elsewhere) even though git already removed its own bookkeeping;
        // residue cleanup below retries the folder and reports the truth instead of a bare git error.
        if (gitError != null)
        {
            logger.LogWarning(
                "Git worktree remove exited {ExitCode} for {WorktreePath} but the worktree is unregistered; checking for leftover files. {Error}",
                exitCode, canonicalWorktreePath, gitError);
        }
        else
        {
            logger.LogInformation("Git worktree removed for {RepoPath}. Path={WorktreePath}, Force={Force}", mainRepositoryPath, canonicalWorktreePath, force);
        }

        var residue = await RemoveWorktreeResidueAsync(
            mainRepositoryPath, canonicalWorktreePath, featureRootPath, featureStorageRoot, isRegisteredWorktree: false, ct);
        return (true, false, null, null, residue);
    }

    /// <summary>
    /// After Git's own worktree removal, deletes any leftover files in <paramref name="worktreePath"/> with a
    /// custom walk (retries, reparse-point-safe) when every safety guard in
    /// <see cref="ValidateResidueRemovalGuards"/> passes, and reports anything left when it does not or when
    /// deletion could not finish (for example a file still open elsewhere). When <paramref name="featureRootPath"/>
    /// becomes empty afterward, it is removed too. Without <paramref name="featureRootPath"/> or
    /// <paramref name="featureStorageRoot"/>, nothing is deleted and only today's folder state is reported.
    /// </summary>
    private async Task<WorktreeResidueResult> RemoveWorktreeResidueAsync(
        string mainRepositoryPath,
        string worktreePath,
        string? featureRootPath,
        string? featureStorageRoot,
        bool isRegisteredWorktree,
        CancellationToken ct)
    {
        var guardFailure = ValidateResidueRemovalGuards(worktreePath, mainRepositoryPath, featureRootPath, featureStorageRoot, isRegisteredWorktree);

        if (!Directory.Exists(worktreePath))
        {
            if (guardFailure == null)
                TryRemoveEmptyFeatureRoot(featureRootPath);
            return WorktreeResidueResult.None;
        }

        if (guardFailure != null)
        {
            logger.LogWarning("Worktree residue cleanup skipped for {WorktreePath}: {Guard}", worktreePath, guardFailure);
            var (skippedCount, skippedSample) = ScanResidueFiles(worktreePath);
            return new WorktreeResidueResult(true, skippedCount, skippedSample, guardFailure);
        }

        await DeleteFolderRecursivelyWithRetryAsync(worktreePath, ct);

        if (Directory.Exists(worktreePath))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(worktreePath).Any())
                    Directory.Delete(worktreePath, false);
            }
            catch
            {
                // Best effort; the residue scan below reports the true state either way.
            }
        }

        if (Directory.Exists(worktreePath))
        {
            var (remainingCount, remainingSample) = ScanResidueFiles(worktreePath);
            var message = remainingCount > 0
                ? "Some files could not be deleted. They may still be open in another program."
                : "The worktree folder could not be removed.";
            logger.LogWarning("Worktree residue remains at {WorktreePath}: {Count} file(s). {Message}", worktreePath, remainingCount, message);
            return new WorktreeResidueResult(true, remainingCount, remainingSample, message);
        }

        TryRemoveEmptyFeatureRoot(featureRootPath);
        return WorktreeResidueResult.None;
    }

    /// <summary>
    /// Deletes <paramref name="featureRootPath"/> only when it exists and is empty. A reparse point at
    /// this level is never treated as empty content and is left alone (should not occur for a Feature root).
    /// </summary>
    private void TryRemoveEmptyFeatureRoot(string? featureRootPath)
    {
        if (string.IsNullOrWhiteSpace(featureRootPath) || !Directory.Exists(featureRootPath))
            return;

        try
        {
            if (!Directory.EnumerateFileSystemEntries(featureRootPath).Any())
            {
                Directory.Delete(featureRootPath, false);
                logger.LogInformation("Removed empty Feature root folder {FeatureRootPath}.", featureRootPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove Feature root folder {FeatureRootPath}.", featureRootPath);
        }
    }

    /// <summary>
    /// Checks every safety guard before any residue deletion is allowed. Returns null when all guards pass,
    /// or a short description of the first guard that failed. Pure (no deletion); callers must still check
    /// disk state separately. Depth is checked before the Feature-root relationship so a path that is both
    /// too shallow and outside the root is reported as too shallow, matching the dedicated test for that guard.
    /// </summary>
    internal static string? ValidateResidueRemovalGuards(
        string worktreePath,
        string mainRepositoryPath,
        string? featureRootPath,
        string? featureStorageRoot,
        bool isRegisteredWorktree)
    {
        if (string.IsNullOrWhiteSpace(featureStorageRoot) || string.IsNullOrWhiteSpace(featureRootPath))
            return "No Feature storage root was provided; residue was only reported, not removed.";

        string normalizedWorktreePath;
        string normalizedMainRepositoryPath;
        string normalizedFeatureRootPath;
        string normalizedFeatureStorageRoot;
        try
        {
            normalizedWorktreePath = NormalizeResiduePath(worktreePath);
            normalizedMainRepositoryPath = NormalizeResiduePath(mainRepositoryPath);
            normalizedFeatureRootPath = NormalizeResiduePath(featureRootPath);
            normalizedFeatureStorageRoot = NormalizeResiduePath(featureStorageRoot);
        }
        catch (Exception ex)
        {
            return $"Could not resolve the Feature storage paths: {ex.Message}";
        }

        // Guard: at least 2 levels below featureStorageRoot (features\<FeatureName>\<Repo>).
        var relativeToStorageRoot = Path.GetRelativePath(normalizedFeatureStorageRoot, normalizedWorktreePath);
        var escapesStorageRoot = relativeToStorageRoot.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativeToStorageRoot);
        if (!escapesStorageRoot)
        {
            var depthSegments = relativeToStorageRoot.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (depthSegments.Length < 2)
                return "Worktree path is too shallow under the Feature storage root.";
        }

        // Guard: worktreePath is strictly under featureStorageRoot\<FeatureName>\, and featureRootPath
        // equals featureStorageRoot\<FeatureName>. Worktrees under the legacy drive-root path fail here.
        var featureRootParent = Path.GetDirectoryName(normalizedFeatureRootPath);
        if (!string.Equals(featureRootParent, normalizedFeatureStorageRoot, StringComparison.OrdinalIgnoreCase)
            || !IsStrictlyUnderResiduePath(normalizedWorktreePath, normalizedFeatureRootPath))
        {
            return "Worktree path is not under this Feature's storage root.";
        }

        // Guard: never the primary repository checkout, and never a path that contains it.
        if (string.Equals(normalizedWorktreePath, normalizedMainRepositoryPath, StringComparison.OrdinalIgnoreCase)
            || IsStrictlyUnderResiduePath(normalizedMainRepositoryPath, normalizedWorktreePath))
        {
            return "Worktree path is or contains the primary repository checkout.";
        }

        // Guard: must not currently be a registered worktree.
        if (isRegisteredWorktree)
            return "Path is still a registered Git worktree.";

        // Guard: a real repository has a .git directory at its top level; a linked worktree does not.
        if (Directory.Exists(Path.Combine(normalizedWorktreePath, ".git")))
            return "Path contains a .git directory and looks like a real repository, not a linked worktree.";

        return null;
    }

    private static string NormalizeResiduePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsStrictlyUnderResiduePath(string path, string potentialAncestor)
    {
        var prefix = potentialAncestor + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Counts files under <paramref name="folderPath"/> (never entering a reparse point) and samples up to 5 relative paths. No deletion.</summary>
    private static (int Count, List<string> Sample) ScanResidueFiles(string folderPath)
    {
        var count = 0;
        var sample = new List<string>();
        VisitResidueEntries(folderPath, entry =>
        {
            count++;
            if (sample.Count < 5)
                sample.Add(Path.GetRelativePath(folderPath, entry.FullName));
        });
        return (count, sample);
    }

    private static void VisitResidueEntries(string folderPath, Action<FileSystemInfo> onFileOrLink)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(folderPath).EnumerateFileSystemInfos();
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            var isReparsePoint = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (entry is DirectoryInfo && !isReparsePoint)
            {
                VisitResidueEntries(entry.FullName, onFileOrLink);
                continue;
            }

            // A file, or a reparse point (junction/symlink): never enter the link, only count/report it.
            onFileOrLink(entry);
        }
    }

    /// <summary>
    /// Deletes everything under <paramref name="folderPath"/> with a custom walk (never
    /// <c>Directory.Delete(path, true)</c>): clears read-only attributes, deletes reparse points
    /// (junctions/symlinks) as the link itself without entering them, and retries each entry up to 5
    /// times (200, 400, 800, 1600, 3200 ms) on <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> (for example a file still open in another program).
    /// Leaves whatever it could not delete in place; the caller reports that as residue.
    /// </summary>
    private static async Task DeleteFolderRecursivelyWithRetryAsync(string folderPath, CancellationToken ct)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(folderPath).EnumerateFileSystemInfos().ToList();
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var isReparsePoint = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (entry is DirectoryInfo && !isReparsePoint)
                await DeleteFolderRecursivelyWithRetryAsync(entry.FullName, ct);

            await DeleteResidueEntryWithRetryAsync(entry, ct);
        }
    }

    private static async Task DeleteResidueEntryWithRetryAsync(FileSystemInfo entry, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= ResidueDeleteRetryDelaysMs.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                    entry.Attributes &= ~FileAttributes.ReadOnly;

                if (entry is DirectoryInfo directory)
                {
                    // Delete the entry itself only (reparse point: the link; otherwise an already-emptied directory).
                    directory.Delete(false);
                }
                else
                {
                    entry.Delete();
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == ResidueDeleteRetryDelaysMs.Length)
                    return;
                await Task.Delay(ResidueDeleteRetryDelaysMs[attempt], ct);
            }
            catch
            {
                return;
            }
        }
    }

    public async Task<WorktreeInspectionResult> InspectWorktreeAsync(
        string mainRepositoryPath,
        string worktreePath,
        string? defaultBranch,
        string? featureBranch,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mainRepositoryPath) || !Directory.Exists(mainRepositoryPath))
            return new WorktreeInspectionResult(false, false, false, null, null, null, null, null, null, null, null, null, null, null, null, "Repository not found.");
        if (string.IsNullOrWhiteSpace(worktreePath))
            return new WorktreeInspectionResult(false, false, false, null, null, null, null, null, null, null, null, null, null, null, null, "worktreePath is required.");

        string canonicalWorktreePath;
        try
        {
            canonicalWorktreePath = Path.GetFullPath(worktreePath);
        }
        catch (Exception ex)
        {
            return new WorktreeInspectionResult(false, false, false, null, null, null, null, null, null, null, null, null, null, null, null, ex.Message);
        }

        var (listOk, worktrees, _, listError) = await ListWorktreesAsync(mainRepositoryPath, ct);
        if (!listOk)
            return new WorktreeInspectionResult(false, false, false, null, null, null, null, null, null, null, null, null, null, null, null, listError ?? "Failed to list worktrees.");

        var registered = GitWorktreeOccupancy.FindByPath(worktrees, canonicalWorktreePath);
        var isRegistered = registered != null;
        var isLocked = registered?.IsLocked ?? false;
        var lockReason = registered?.LockReason;

        if (!Directory.Exists(canonicalWorktreePath))
        {
            return new WorktreeInspectionResult(isRegistered, false, isLocked, lockReason, null, null, null, null, null, null, null, null, null, null, null, null);
        }

        var headSha = await reader.GetHeadCommitAsync(canonicalWorktreePath, ct);
        var branch = await reader.GetCurrentBranchNameAsync(canonicalWorktreePath, ct);

        var (statusOk, staged, unstaged, untracked, conflicts, statusError) = await ProbeWorktreeStatusAsync(canonicalWorktreePath, ct);
        bool? isDirty = statusOk ? staged + unstaged + untracked + conflicts > 0 : null;

        bool? hasUpstream = null;
        int? aheadOfUpstream = null;
        int? behindUpstream = null;
        if (!string.IsNullOrWhiteSpace(branch))
        {
            var (upstreamKnown, ahead, behind) = await ProbeUpstreamCountsAsync(canonicalWorktreePath, branch, ct);
            hasUpstream = upstreamKnown;
            aheadOfUpstream = ahead;
            behindUpstream = behind;
        }

        // A Feature worktree carries its own persisted "divergence base" (the branch it was actually
        // created from - its parent, which can itself be another unmerged, not-yet-pushed Feature
        // branch, not necessarily the repository's true default branch). "Ahead of default" must be
        // judged against that parent when one was recorded, or deleting a nested Feature branch
        // would be reported as losing every commit the parent branch is already ahead of main by,
        // even though those commits stay reachable from the parent branch and are never actually
        // lost. Falls back to the repository's true default branch when no divergence base was
        // recorded, or it no longer resolves to an existing ref.
        var aheadOfDefaultCompareRef = await ResolveAheadOfDefaultCompareRefAsync(canonicalWorktreePath, defaultBranch, ct);

        var aheadOfDefault = await ProbeAheadOfDefaultAsync(canonicalWorktreePath, aheadOfDefaultCompareRef, ct);

        bool? featureBranchExists = null;
        string? featureBranchSha = null;
        int? featureBranchAheadOfDefault = null;
        bool? featureBranchHasUpstream = null;
        int? featureBranchAheadOfUpstream = null;
        if (!string.IsNullOrWhiteSpace(featureBranch))
        {
            featureBranchSha = await GetRevisionShaAsync(canonicalWorktreePath, $"refs/heads/{featureBranch}", ct);
            featureBranchExists = featureBranchSha != null;
            if (featureBranchExists == true)
            {
                featureBranchAheadOfDefault = await ProbeAheadOfDefaultForRefAsync(
                    canonicalWorktreePath, aheadOfDefaultCompareRef, $"refs/heads/{featureBranch}", ct);
                var (featureUpstreamKnown, featureAhead) = await ProbeFeatureBranchUpstreamCountAsync(
                    canonicalWorktreePath, featureBranch, ct);
                featureBranchHasUpstream = featureUpstreamKnown;
                featureBranchAheadOfUpstream = featureAhead;
            }
        }

        return new WorktreeInspectionResult(
            isRegistered,
            true,
            isLocked,
            lockReason,
            headSha,
            branch,
            isDirty,
            statusOk ? staged : null,
            statusOk ? unstaged : null,
            statusOk ? untracked : null,
            statusOk ? conflicts : null,
            hasUpstream,
            aheadOfUpstream,
            behindUpstream,
            aheadOfDefault,
            statusOk ? null : statusError,
            featureBranchExists,
            featureBranchSha,
            featureBranchAheadOfDefault,
            featureBranchHasUpstream,
            featureBranchAheadOfUpstream);
    }

    /// <summary>
    /// Resolves the ref to count "ahead of default" against: this worktree's own persisted
    /// divergence base (<see cref="IGitRepositoryReader.GetDivergenceBaseBranchAsync"/>, the Feature's actual parent
    /// branch) when one was recorded and still exists - checked as a local branch name first since a
    /// parent that is itself an unmerged, unpushed Feature branch never has an <c>origin/</c> ref -
    /// falling back to <see cref="OriginDefaultRef.ToOriginBranchRef"/> of <paramref name="defaultBranch"/> otherwise
    /// (same resolution order as <see cref="ResolveNoUpstreamCompareRefAsync"/>/GetCommitCountsCommand).
    /// </summary>
    private async Task<string?> ResolveAheadOfDefaultCompareRefAsync(string repoPath, string? defaultBranch, CancellationToken ct)
    {
        var divergenceBase = await reader.GetDivergenceBaseBranchAsync(repoPath, ct);
        if (!string.IsNullOrWhiteSpace(divergenceBase))
        {
            var local = divergenceBase.Trim();
            if (local.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
                local = local["origin/".Length..];
            if (await reader.RefExistsAsync(repoPath, local, ct))
                return local;

            var originRef = $"origin/{local}";
            if (await reader.RefExistsAsync(repoPath, originRef, ct))
                return originRef;
        }

        return OriginDefaultRef.ToOriginBranchRef(defaultBranch);
    }

    /// <summary>
    /// Like <see cref="ProbeAheadOfDefaultAsync"/> but against an arbitrary ref instead of always
    /// HEAD, so a Feature branch can be judged without checking it out (09 SB-2, plan unit I1).
    /// </summary>
    private async Task<int?> ProbeAheadOfDefaultForRefAsync(string repoPath, string? defaultRef, string compareRef, CancellationToken ct)
    {
        if (defaultRef == null || !await reader.RefExistsAsync(repoPath, defaultRef, ct))
            return null;

        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            $"rev-list --count {defaultRef}..{compareRef}",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        if (exitCode != 0)
        {
            logger.LogWarning("Git rev-list (InspectWorktree Feature branch ahead of default) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return null;
        }

        return int.TryParse((stdout ?? "").Trim(), out var count) ? count : (int?)null;
    }

    /// <summary>
    /// Like <see cref="ProbeUpstreamCountsAsync"/> but for an arbitrary local branch instead of
    /// always HEAD, so a Feature branch's upstream state can be judged without checking it out
    /// (09 SB-2, plan unit I1). Behind-count is not needed by any caller, so it is not computed.
    /// </summary>
    private async Task<(bool HasUpstream, int? Ahead)> ProbeFeatureBranchUpstreamCountAsync(string repoPath, string branchName, CancellationToken ct)
    {
        var upstreamRef = await reader.GetUpstreamRefAsync(repoPath, branchName, ct);
        if (string.IsNullOrWhiteSpace(upstreamRef) || !await reader.RefExistsAsync(repoPath, upstreamRef, ct))
            return (false, null);

        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            $"rev-list --left-right --count {upstreamRef}...refs/heads/{branchName}",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        if (exitCode != 0)
        {
            logger.LogWarning("Git rev-list --left-right (InspectWorktree Feature branch upstream count) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return (true, null);
        }

        var parts = (stdout ?? "").Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
        var ahead = parts.Length >= 2 && int.TryParse(parts[1], out var a) ? a : (int?)null;
        return (true, ahead);
    }

    /// <summary>
    /// SHA of a revision if it exists, or null. Uses <c>rev-parse --verify --quiet</c> so a missing
    /// ref is a silent non-zero exit instead of a visible Git error (same reasoning as <see cref="IGitRepositoryReader.RefExistsAsync"/>).
    /// </summary>
    private async Task<string?> GetRevisionShaAsync(string repoPath, string revision, CancellationToken ct)
    {
        var (exitCode, stdout, _) = await runner.RunAsync(
            "git",
            $"rev-parse --verify --quiet {revision}",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        if (exitCode != 0)
            return null;

        var sha = (stdout ?? "").Trim();
        return string.IsNullOrWhiteSpace(sha) ? null : sha;
    }

    private async Task<(bool Success, int Staged, int Unstaged, int Untracked, int Conflicts, string? Error)> ProbeWorktreeStatusAsync(
        string repoPath,
        CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            "--no-optional-locks status --porcelain=v1",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false,
            intent: GitLockIntent.Read);
        if (exitCode != 0)
            return (false, 0, 0, 0, 0, CombineOutput(stdout, stderr) ?? "git status failed");

        var staged = 0;
        var unstaged = 0;
        var untracked = 0;
        var conflicts = 0;
        foreach (var line in (stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 2)
                continue;
            var x = line[0];
            var y = line[1];
            if (x == '?' && y == '?')
            {
                untracked++;
                continue;
            }
            if (IsUnmergedStatusCode(x, y))
            {
                conflicts++;
                continue;
            }
            if (x != ' ')
                staged++;
            if (y != ' ')
                unstaged++;
        }

        return (true, staged, unstaged, untracked, conflicts, null);
    }

    private static bool IsUnmergedStatusCode(char x, char y)
        => x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D');

    /// <summary>Ahead/behind strictly against the branch's configured upstream; no fallback to the default branch
    /// or a Feature divergence base, unlike <see cref="ProbeCommitCountsAsync"/>, so the InspectWorktree caller
    /// gets a clean null when there is no upstream instead of a value computed against something else.</summary>
    private async Task<(bool HasUpstream, int? Ahead, int? Behind)> ProbeUpstreamCountsAsync(
        string repoPath,
        string branchName,
        CancellationToken ct)
    {
        var upstreamRef = await reader.GetUpstreamRefAsync(repoPath, branchName, ct);
        if (string.IsNullOrWhiteSpace(upstreamRef) || !await reader.RefExistsAsync(repoPath, upstreamRef, ct))
            return (false, null, null);

        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            $"rev-list --left-right --count {upstreamRef}...HEAD",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        if (exitCode != 0)
        {
            logger.LogWarning("Git rev-list --left-right (InspectWorktree upstream counts) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return (true, null, null);
        }

        var parts = (stdout ?? "").Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
        var behind = parts.Length >= 1 && int.TryParse(parts[0], out var b) ? b : (int?)null;
        var ahead = parts.Length >= 2 && int.TryParse(parts[1], out var a) ? a : (int?)null;
        return (true, ahead, behind);
    }

    private async Task<int?> ProbeAheadOfDefaultAsync(string repoPath, string? defaultRef, CancellationToken ct)
    {
        if (defaultRef == null || !await reader.RefExistsAsync(repoPath, defaultRef, ct))
            return null;

        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            $"rev-list --count {defaultRef}..HEAD",
            repoPath,
            ct,
            streamStderrAsStdout: true,
            mirrorFailureOutputAsStderr: false);
        if (exitCode != 0)
        {
            logger.LogWarning("Git rev-list (InspectWorktree ahead of default) failed for {RepoPath}. ExitCode={ExitCode}, Stdout={Stdout}, Stderr={Stderr}", repoPath, exitCode, stdout, stderr);
            return null;
        }

        return int.TryParse((stdout ?? "").Trim(), out var count) ? count : (int?)null;
    }
}
