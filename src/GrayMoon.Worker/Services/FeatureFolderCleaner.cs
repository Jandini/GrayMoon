using GrayMoon.Worker.Abstractions;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services;

public enum FeatureFolderCleanupOutcome
{
    /// <summary>The folder is gone (or was never there).</summary>
    Removed = 0,
    /// <summary>Something is still in use; the folder is marked with <see cref="PendingDeleteMarker"/> for a later pass.</summary>
    PendingDeletion = 1,
    /// <summary>A safety guard failed; nothing was deleted or marked.</summary>
    Refused = 2,
    /// <summary>The caller asked to clean only a marked folder, and this one carries no matching marker; nothing was touched.</summary>
    NotMarked = 3,
}

public sealed record FeatureFolderCleanupResult(FeatureFolderCleanupOutcome Outcome, int RemainingFileCount = 0, string? Message = null);

/// <summary>
/// Deletes a removed Feature's leftover folder (<c>featureStorageRoot\&lt;Feature&gt;</c>), or marks it pending deletion
/// when files are still in use, so a later background pass can finish the job without asking anyone. Never ends or
/// inspects a process. Only ever deletes a direct child of the Feature storage root that holds no Git repository and no
/// still-registered worktree.
/// </summary>
public sealed class FeatureFolderCleaner(ILogger<FeatureFolderCleaner> logger, IRepositoryAccess access)
{
    private const string FeaturesFolderName = "features";

    /// <summary>How many folder levels a Feature name with <c>/</c> may span when the sweep looks for marked folders.</summary>
    private const int MaxFeatureNameDepth = 8;

    public async Task<FeatureFolderCleanupResult> CleanupAsync(
        string? featureStorageRoot,
        string? featureRootPath,
        string? workspaceName,
        string? featureName,
        bool retry,
        bool requireMarker,
        CancellationToken ct)
    {
        var guardFailure = ValidatePaths(featureStorageRoot, featureRootPath);
        if (guardFailure != null)
        {
            logger.LogWarning("Feature folder cleanup refused for {FeatureRootPath}: {Guard}", featureRootPath, guardFailure);
            return new FeatureFolderCleanupResult(FeatureFolderCleanupOutcome.Refused, Message: guardFailure);
        }

        var root = Normalize(featureRootPath!);
        if (!Directory.Exists(root))
        {
            TryRemoveEmptyParents(Normalize(featureStorageRoot!), root);
            return new FeatureFolderCleanupResult(FeatureFolderCleanupOutcome.Removed);
        }

        var treeFailure = ValidateTree(Normalize(featureStorageRoot!), root);
        if (treeFailure != null)
        {
            logger.LogWarning("Feature folder cleanup refused for {FeatureRootPath}: {Guard}", root, treeFailure);
            return new FeatureFolderCleanupResult(FeatureFolderCleanupOutcome.Refused, Message: treeFailure);
        }

        var existingMarker = PendingDeleteMarker.TryRead(root);
        if (requireMarker && (existingMarker == null || !MarkerNames(existingMarker, root)))
            return new FeatureFolderCleanupResult(FeatureFolderCleanupOutcome.NotMarked);

        // The Worker's own handles (watchers, git processes) never block this delete; the marker is for foreign programs.
        IRepositoryExclusiveScope? removalScope = null;
        try
        {
            removalScope = await access.AcquireExclusiveAsync([root], ct);
        }
        catch (RepositoryAccessException ex)
        {
            logger.LogError(ex, "Could not release the Worker's own handles on {FeatureRootPath}; leaving it marked.", root);
        }

        using var scope = removalScope;
        if (scope != null)
        {
            await GitWorktreeService.DeleteFolderRecursivelyWithRetryAsync(root, ct, retry);
            TryDeleteEmptyFolder(root);
        }

        if (!Directory.Exists(root))
        {
            logger.LogInformation("Removed leftover Feature folder {FeatureRootPath}.", root);
            TryRemoveEmptyParents(Normalize(featureStorageRoot!), root);
            return new FeatureFolderCleanupResult(FeatureFolderCleanupOutcome.Removed);
        }

        var (remainingCount, _) = GitWorktreeService.ScanResidueFiles(root);
        var marker = new PendingDeleteMarker(
            root,
            string.IsNullOrWhiteSpace(workspaceName) ? existingMarker?.Workspace : workspaceName,
            string.IsNullOrWhiteSpace(featureName) ? existingMarker?.Feature : featureName,
            existingMarker?.MarkedUtc ?? DateTime.UtcNow);
        WriteMarkers(root, marker);

        logger.LogInformation(
            "Feature folder {FeatureRootPath} is still in use ({Count} file(s) left); marked pending deletion.", root, remainingCount);
        return new FeatureFolderCleanupResult(
            FeatureFolderCleanupOutcome.PendingDeletion,
            remainingCount,
            "Some files are still in use. The folder is marked for deletion and will be removed later.");
    }

    /// <summary>
    /// Folders under <paramref name="featureStorageRoot"/> carrying a marker whose <c>folder</c> is that same folder, except
    /// Features that still exist (<paramref name="excludeFeatureNames"/>, matched against the path below the root with
    /// <c>/</c> separators). Only descends through plain grouping folders (a Feature name with <c>/</c>): never into a
    /// marked folder, a Git checkout, or a link, and never deeper than a Feature name can nest.
    /// </summary>
    public IReadOnlyList<string> FindMarkedFolders(string featureStorageRoot, IReadOnlyCollection<string> excludeFeatureNames)
    {
        var storageRoot = Normalize(featureStorageRoot);
        if (!Directory.Exists(storageRoot) || IsReparsePoint(storageRoot))
            return [];

        var excluded = new HashSet<string>(excludeFeatureNames.Select(n => n.Trim('/')), StringComparer.OrdinalIgnoreCase);
        var marked = new List<string>();
        var pending = new Stack<(string Folder, int Depth)>();
        pending.Push((storageRoot, 0));
        while (pending.Count > 0)
        {
            var (folder, depth) = pending.Pop();
            IEnumerable<DirectoryInfo> children;
            try
            {
                children = new DirectoryInfo(folder).EnumerateDirectories().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not list {Folder} while looking for pending-delete folders.", folder);
                continue;
            }

            foreach (var child in children)
            {
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;

                var featureName = Path.GetRelativePath(storageRoot, child.FullName).Replace(Path.DirectorySeparatorChar, '/');
                if (excluded.Contains(featureName))
                    continue;

                var marker = PendingDeleteMarker.TryRead(child.FullName);
                if (marker != null)
                {
                    if (MarkerNames(marker, child.FullName))
                        marked.Add(child.FullName);
                    else
                        logger.LogWarning("Ignoring pending-delete marker in {Folder}: it names {MarkerFolder}.", child.FullName, marker.Folder);
                    continue;
                }

                if (depth + 1 < MaxFeatureNameDepth && !Path.Exists(Path.Combine(child.FullName, ".git")))
                    pending.Push((child.FullName, depth + 1));
            }
        }

        return marked;
    }

    private static bool MarkerNames(PendingDeleteMarker marker, string folder)
    {
        try
        {
            return string.Equals(Normalize(marker.Folder), Normalize(folder), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Pure path checks: both paths absolute, <paramref name="featureStorageRoot"/> a GrayMoon <c>features</c> folder, and
    /// <paramref name="featureRootPath"/> below it. Returns null when they pass.
    /// </summary>
    internal static string? ValidatePaths(string? featureStorageRoot, string? featureRootPath)
    {
        if (string.IsNullOrWhiteSpace(featureStorageRoot) || string.IsNullOrWhiteSpace(featureRootPath))
            return "Feature storage root and Feature folder are required.";
        if (!Path.IsPathFullyQualified(featureStorageRoot) || !Path.IsPathFullyQualified(featureRootPath))
            return "Feature storage root and Feature folder must be absolute paths.";

        string storageRoot;
        string root;
        try
        {
            storageRoot = Normalize(featureStorageRoot);
            root = Normalize(featureRootPath);
        }
        catch (Exception ex)
        {
            return $"Could not resolve the Feature folder: {ex.Message}";
        }

        if (string.Equals(Path.GetDirectoryName(storageRoot), storageRoot, StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(storageRoot) == null)
        {
            return "The Feature storage root cannot be a drive or file system root.";
        }

        // GrayMoon always keeps Features under "<storage>\<Workspace>\features"; anything else is not ours to delete.
        if (!string.Equals(Path.GetFileName(storageRoot), FeaturesFolderName, StringComparison.OrdinalIgnoreCase))
            return "The Feature storage root is not a GrayMoon features folder.";

        // A Feature name may contain '/', which nests its folder (features\team\login), so any depth below the root is fine.
        if (!IsStrictlyUnder(root, storageRoot))
            return "The folder is not under the Feature storage root.";

        return null;
    }

    /// <summary>
    /// Disk checks before anything is deleted: no reparse point at the storage root or the Feature folder, no
    /// <c>.git</c> folder anywhere (a real repository), and no <c>.git</c> file that still points at an existing
    /// worktree registration. Never enters a reparse point. Returns null when they pass.
    /// </summary>
    internal static string? ValidateTree(string featureStorageRoot, string featureRootPath)
    {
        if (IsReparsePoint(featureStorageRoot) || IsReparsePoint(featureRootPath))
            return "The Feature folder or its storage root is a link.";

        var pending = new Stack<string>();
        pending.Push(featureRootPath);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            var gitPath = Path.Combine(folder, ".git");
            if (Directory.Exists(gitPath))
                return $"Contains a Git repository ({Path.GetRelativePath(featureRootPath, folder)}).";
            if (File.Exists(gitPath) && IsLiveWorktreePointer(folder, gitPath))
                return $"Contains a Git worktree that is still registered ({Path.GetRelativePath(featureRootPath, folder)}).";

            IEnumerable<DirectoryInfo> children;
            try
            {
                children = new DirectoryInfo(folder).EnumerateDirectories().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (!child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    pending.Push(child.FullName);
            }
        }

        return null;
    }

    /// <summary>
    /// True when the <c>.git</c> file's <c>gitdir:</c> target still exists (Git still knows this worktree), or when the
    /// file cannot be read or understood; only a pointer to a registration that is gone is safe to delete.
    /// </summary>
    private static bool IsLiveWorktreePointer(string folder, string gitFilePath)
    {
        try
        {
            var line = File.ReadLines(gitFilePath).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
            if (line == null)
                return true;
            var target = line["gitdir:".Length..].Trim();
            if (target.Length == 0)
                return true;
            var resolved = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(folder, target));
            return Directory.Exists(resolved);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>The marker at the top, plus a copy in each leftover repository folder so a tool working there sees it.</summary>
    private void WriteMarkers(string root, PendingDeleteMarker marker)
    {
        var content = marker.Render();
        TryWriteMarker(Path.Combine(root, PendingDeleteMarker.FileName), content);

        IEnumerable<DirectoryInfo> children;
        try
        {
            children = new DirectoryInfo(root).EnumerateDirectories().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            if (!child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                TryWriteMarker(Path.Combine(child.FullName, PendingDeleteMarker.FileName), content);
        }
    }

    private void TryWriteMarker(string path, string content)
    {
        try
        {
            File.WriteAllText(path, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write pending-delete marker {MarkerPath}.", path);
        }
    }

    private static void TryDeleteEmptyFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use (for example a program's current folder); the caller marks it.
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// After a nested Feature folder (features\team\login) is gone, removes the parents it leaves empty, up to but never
    /// including the storage root.
    /// </summary>
    private void TryRemoveEmptyParents(string storageRoot, string removedFolder)
    {
        var parent = Path.GetDirectoryName(removedFolder);
        while (parent != null && IsStrictlyUnder(parent, storageRoot) && !IsReparsePoint(parent))
        {
            try
            {
                if (!Directory.Exists(parent) || Directory.EnumerateFileSystemEntries(parent).Any())
                    return;
                Directory.Delete(parent, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not remove empty folder {Folder}.", parent);
                return;
            }

            parent = Path.GetDirectoryName(parent);
        }
    }

    private static bool IsStrictlyUnder(string path, string ancestor)
    {
        var prefix = ancestor + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
