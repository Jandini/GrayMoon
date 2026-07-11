using System.Text;
using GrayMoon.Agent.Abstractions;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Services.GitChanges;

/// <summary>
/// Native-git-CLI implementation of <see cref="IRepositoryGitChangesService"/>. Every git invocation goes
/// through <see cref="GitProcessRunner"/>'s <c>ArgumentList</c> overload (never an interpolated argument
/// string) and every path is validated with <see cref="GitRepositoryPathValidator"/> before use.
/// </summary>
public sealed class GitCliRepositoryGitChangesService(GitProcessRunner runner, ILogger<GitCliRepositoryGitChangesService> logger)
    : IRepositoryGitChangesService
{
    private const int SoftSizeLimitBytes = 5 * 1024 * 1024;

    public async Task<GitChangeStatusResult> GetStatusAsync(string repoPath, long snapshotVersion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return new GitChangeStatusResult { Success = false, ErrorCode = "RepositoryNotFound", ErrorMessage = "Repository not found." };
        }

        var (exitCode, stdout, stderr) = await runner.RunAsync(
            "git",
            ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all"],
            repoPath,
            null,
            cancellationToken);

        if (exitCode != 0)
        {
            var error = (stderr ?? stdout ?? "git status failed").Trim();
            logger.LogError("Git status failed for {RepoPath}. ExitCode={ExitCode}, Stderr={Stderr}", repoPath, exitCode, error);
            return new GitChangeStatusResult { Success = false, ErrorCode = "GitStatusFailed", ErrorMessage = error };
        }

        var parsed = GitPorcelainV2Parser.Parse(stdout);
        var (isMerging, isRebasing, isCherryPicking) = await GetOperationStateAsync(repoPath, cancellationToken);

        var snapshot = new GitChangeSnapshot
        {
            Version = snapshotVersion,
            BranchName = parsed.BranchName,
            HeadCommit = parsed.HeadCommit,
            IsDetachedHead = parsed.IsDetachedHead,
            IsUnbornBranch = parsed.IsUnbornBranch,
            IsMerging = isMerging,
            IsRebasing = isRebasing,
            IsCherryPicking = isCherryPicking,
            Changes = parsed.Changes,
            ScannedAt = DateTimeOffset.UtcNow,
        };

        return new GitChangeStatusResult { Success = true, Snapshot = snapshot };
    }

    public async Task<GitDiffDocument> GetDiffAsync(string repoPath, GitDiffRequest request, CancellationToken cancellationToken)
    {
        var validation = GitRepositoryPathValidator.Validate(repoPath, request.Path);
        if (!validation.IsValid)
        {
            return new GitDiffDocument
            {
                Path = request.Path ?? string.Empty,
                Comparison = request.Comparison,
                State = GitDiffContentState.Error,
                ErrorMessage = validation.ErrorMessage,
            };
        }

        var relativePath = validation.NormalizedRelativePath!;
        var fullPath = validation.FullPath!;
        var languageId = MonacoLanguageMapper.GetLanguageId(relativePath);

        if (await IsBinaryAsync(repoPath, relativePath, request.Comparison, cancellationToken))
        {
            var (originalSize, modifiedSize) = await GetBinarySizesAsync(repoPath, relativePath, request.Comparison, fullPath, cancellationToken);
            return new GitDiffDocument
            {
                Path = relativePath,
                Comparison = request.Comparison,
                State = GitDiffContentState.Binary,
                OriginalSizeBytes = originalSize,
                ModifiedSizeBytes = modifiedSize,
                LanguageId = languageId,
            };
        }

        string? originalContent;
        string? modifiedContent;

        if (request.Comparison == GitDiffComparison.Unstaged)
        {
            originalContent = await ShowIndexContentAsync(repoPath, relativePath, cancellationToken);
            modifiedContent = await ReadWorkingTreeContentAsync(fullPath, cancellationToken);
        }
        else
        {
            originalContent = await ShowRefContentAsync(repoPath, "HEAD", relativePath, cancellationToken);
            modifiedContent = await ShowIndexContentAsync(repoPath, relativePath, cancellationToken);
        }

        if (ContainsBinaryMarker(originalContent) || ContainsBinaryMarker(modifiedContent))
        {
            return new GitDiffDocument
            {
                Path = relativePath,
                Comparison = request.Comparison,
                State = GitDiffContentState.Binary,
                OriginalSizeBytes = originalContent != null ? Encoding.UTF8.GetByteCount(originalContent) : null,
                ModifiedSizeBytes = modifiedContent != null ? Encoding.UTF8.GetByteCount(modifiedContent) : null,
                LanguageId = languageId,
            };
        }

        var originalBytes = originalContent != null ? Encoding.UTF8.GetByteCount(originalContent) : 0;
        var modifiedBytes = modifiedContent != null ? Encoding.UTF8.GetByteCount(modifiedContent) : 0;

        if (originalBytes > SoftSizeLimitBytes || modifiedBytes > SoftSizeLimitBytes)
        {
            return new GitDiffDocument
            {
                Path = relativePath,
                Comparison = request.Comparison,
                State = GitDiffContentState.TooLarge,
                OriginalSizeBytes = originalBytes,
                ModifiedSizeBytes = modifiedBytes,
                LanguageId = languageId,
            };
        }

        var state = originalContent == null && modifiedContent != null
            ? GitDiffContentState.NewFile
            : originalContent != null && modifiedContent == null
                ? GitDiffContentState.DeletedFile
                : GitDiffContentState.Normal;

        return new GitDiffDocument
        {
            Path = relativePath,
            Comparison = request.Comparison,
            State = state,
            OriginalContent = originalContent ?? string.Empty,
            ModifiedContent = modifiedContent ?? string.Empty,
            OriginalSizeBytes = originalBytes,
            ModifiedSizeBytes = modifiedBytes,
            LanguageId = languageId,
        };
    }

    public async Task<GitMutationResult> StageAsync(string repoPath, GitStageOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return new GitMutationResult { Success = false, ErrorCode = "RepositoryNotFound", ErrorMessage = "Repository not found." };
        }

        if (IsWholeRepositoryScope(request.Scope))
        {
            var (exitCode, stdout, stderr) = await runner.RunAsync("git", ["add", "--all"], repoPath, null, cancellationToken);
            if (exitCode != 0)
            {
                return await MutationFailureAsync(repoPath, "StageFailed", (stderr ?? stdout ?? "git add failed").Trim(), nextSnapshotVersion, cancellationToken);
            }
        }
        else
        {
            var normalized = ValidateAndNormalizePaths(repoPath, request.Paths, out var pathError);
            if (pathError != null)
            {
                return new GitMutationResult { Success = false, ErrorCode = "InvalidPath", ErrorMessage = pathError };
            }

            if (normalized.Count == 0)
            {
                return new GitMutationResult { Success = false, ErrorCode = "NoPaths", ErrorMessage = "No paths to stage." };
            }

            var (exitCode, stdout, stderr) = await RunPathspecOperationAsync(repoPath, ["add"], normalized, cancellationToken);
            if (exitCode != 0)
            {
                return await MutationFailureAsync(repoPath, "StageFailed", (stderr ?? stdout ?? "git add failed").Trim(), nextSnapshotVersion, cancellationToken);
            }
        }

        return await MutationSuccessAsync(repoPath, nextSnapshotVersion, cancellationToken);
    }

    public async Task<GitMutationResult> UnstageAsync(string repoPath, GitStageOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return new GitMutationResult { Success = false, ErrorCode = "RepositoryNotFound", ErrorMessage = "Repository not found." };
        }

        if (IsWholeRepositoryScope(request.Scope))
        {
            var (exitCode, stdout, stderr) = await UnstageAllAsync(repoPath, cancellationToken);
            if (exitCode != 0)
            {
                return await MutationFailureAsync(repoPath, "UnstageFailed", (stderr ?? stdout ?? "git restore failed").Trim(), nextSnapshotVersion, cancellationToken);
            }
        }
        else
        {
            var normalized = ValidateAndNormalizePaths(repoPath, request.Paths, out var pathError);
            if (pathError != null)
            {
                return new GitMutationResult { Success = false, ErrorCode = "InvalidPath", ErrorMessage = pathError };
            }

            if (normalized.Count == 0)
            {
                return new GitMutationResult { Success = false, ErrorCode = "NoPaths", ErrorMessage = "No paths to unstage." };
            }

            var (exitCode, stdout, stderr) = await UnstagePathsAsync(repoPath, normalized, cancellationToken);
            if (exitCode != 0)
            {
                return await MutationFailureAsync(repoPath, "UnstageFailed", (stderr ?? stdout ?? "git restore failed").Trim(), nextSnapshotVersion, cancellationToken);
            }
        }

        return await MutationSuccessAsync(repoPath, nextSnapshotVersion, cancellationToken);
    }

    public async Task<GitCommitResult> CommitAsync(string repoPath, GitCommitOperationRequest request, long nextSnapshotVersion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return new GitCommitResult { Success = false, ErrorCode = "RepositoryNotFound", ErrorMessage = "Repository not found." };
        }

        if (string.IsNullOrWhiteSpace(request.CommitMessage))
        {
            return new GitCommitResult { Success = false, ErrorCode = "EmptyMessage", ErrorMessage = "Commit message is required." };
        }

        if (request.StageAllFirst)
        {
            var (addExit, addOut, addErr) = await runner.RunAsync("git", ["add", "--all"], repoPath, null, cancellationToken);
            if (addExit != 0)
            {
                var snapshot = await TryGetSnapshotAsync(repoPath, nextSnapshotVersion, cancellationToken);
                return new GitCommitResult { Success = false, ErrorCode = "StageFailed", ErrorMessage = (addErr ?? addOut ?? "git add failed").Trim(), Snapshot = snapshot };
            }
        }

        var (stagedExit, _, _) = await runner.RunAsync("git", ["diff", "--cached", "--quiet"], repoPath, null, cancellationToken);
        if (stagedExit == 0)
        {
            var snapshot = await TryGetSnapshotAsync(repoPath, nextSnapshotVersion, cancellationToken);
            return new GitCommitResult { Success = false, ErrorCode = "NothingStaged", ErrorMessage = "No staged changes to commit.", Snapshot = snapshot };
        }

        var messageBytes = Encoding.UTF8.GetBytes(NormalizeCommitMessage(request.CommitMessage));
        var (commitExit, commitOut, commitErr) = await runner.RunAsync("git", ["commit", "-F", "-"], repoPath, messageBytes, cancellationToken);
        if (commitExit != 0)
        {
            var snapshot = await TryGetSnapshotAsync(repoPath, nextSnapshotVersion, cancellationToken);
            var error = (commitErr ?? commitOut ?? "git commit failed").Trim();
            logger.LogError("Git commit failed for {RepoPath}. ExitCode={ExitCode}, Stderr={Stderr}", repoPath, commitExit, error);
            return new GitCommitResult { Success = false, ErrorCode = "CommitFailed", ErrorMessage = error, Snapshot = snapshot };
        }

        var commitSha = await GetHeadCommitShaAsync(repoPath, cancellationToken);
        var finalSnapshot = await TryGetSnapshotAsync(repoPath, nextSnapshotVersion, cancellationToken);
        logger.LogInformation("Git commit completed for {RepoPath}: {CommitSha}", repoPath, commitSha);
        return new GitCommitResult { Success = true, CommitSha = commitSha, Snapshot = finalSnapshot };
    }

    private static bool IsWholeRepositoryScope(GitChangeOperationScope scope) =>
        scope is GitChangeOperationScope.Repository or GitChangeOperationScope.EntireSection;

    private async Task<(int ExitCode, string? Stdout, string? Stderr)> RunPathspecOperationAsync(
        string repoPath,
        IReadOnlyList<string> commandPrefix,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var stdinBytes = GitPathspecStdinWriter.BuildNulDelimitedUtf8(paths);
        var args = commandPrefix.Concat(["--pathspec-from-file=-", "--pathspec-file-nul"]).ToArray();
        var result = await runner.RunAsync("git", args, repoPath, stdinBytes, cancellationToken);
        if (result.ExitCode == 0)
        {
            return result;
        }

        // Older git (< 2.25) does not support --pathspec-from-file; fall back to bounded, char-count-limited
        // plain argument batches so a single invocation never exceeds Windows command-line length limits.
        if (!IsUnknownOptionError(result.Stderr, result.Stdout))
        {
            return result;
        }

        return await RunBoundedBatchesAsync(repoPath, commandPrefix, paths, cancellationToken);
    }

    private async Task<(int ExitCode, string? Stdout, string? Stderr)> UnstagePathsAsync(
        string repoPath,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var stdinBytes = GitPathspecStdinWriter.BuildNulDelimitedUtf8(paths);
        var result = await runner.RunAsync(
            "git",
            ["restore", "--staged", "--pathspec-from-file=-", "--pathspec-file-nul"],
            repoPath,
            stdinBytes,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return result;
        }

        // `restore` fails on an unborn branch (no HEAD to restore from); `reset` clears the index safely
        // even when HEAD does not exist yet, and also supports --pathspec-from-file as a compatibility path.
        var freshStdinBytes = GitPathspecStdinWriter.BuildNulDelimitedUtf8(paths);
        var resetResult = await runner.RunAsync(
            "git",
            ["reset", "--pathspec-from-file=-", "--pathspec-file-nul"],
            repoPath,
            freshStdinBytes,
            cancellationToken);

        if (resetResult.ExitCode == 0 || !IsUnknownOptionError(resetResult.Stderr, resetResult.Stdout))
        {
            return resetResult;
        }

        return await RunBoundedBatchesAsync(repoPath, ["reset"], paths, cancellationToken);
    }

    private async Task<(int ExitCode, string? Stdout, string? Stderr)> UnstageAllAsync(string repoPath, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("git", ["restore", "--staged", ":/"], repoPath, null, cancellationToken);
        if (result.ExitCode == 0)
        {
            return result;
        }

        // Unborn branch (no HEAD yet) - `git reset` with no arguments clears the index safely in that case too.
        return await runner.RunAsync("git", ["reset"], repoPath, null, cancellationToken);
    }

    private async Task<(int ExitCode, string? Stdout, string? Stderr)> RunBoundedBatchesAsync(
        string repoPath,
        IReadOnlyList<string> commandPrefix,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var batches = GitPathspecStdinWriter.BuildBoundedBatches(paths);
        for (var i = 0; i < batches.Count; i++)
        {
            var args = commandPrefix.Concat(["--"]).Concat(batches[i]).ToArray();
            var result = await runner.RunAsync("git", args, repoPath, null, cancellationToken);
            if (result.ExitCode != 0)
            {
                var error = $"Batch {i + 1} of {batches.Count} failed: {(result.Stderr ?? result.Stdout ?? "git failed").Trim()}";
                return (result.ExitCode, result.Stdout, error);
            }
        }

        return (0, null, null);
    }

    private static bool IsUnknownOptionError(string? stderr, string? stdout)
    {
        var text = ((stderr ?? string.Empty) + " " + (stdout ?? string.Empty));
        return text.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unrecognized argument", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GitMutationResult> MutationFailureAsync(string repoPath, string errorCode, string errorMessage, long nextSnapshotVersion, CancellationToken cancellationToken)
    {
        logger.LogError("Git changes mutation failed for {RepoPath}: {ErrorCode} {ErrorMessage}", repoPath, errorCode, errorMessage);
        var snapshot = await TryGetSnapshotAsync(repoPath, nextSnapshotVersion, cancellationToken);
        return new GitMutationResult { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage, Snapshot = snapshot };
    }

    private async Task<GitMutationResult> MutationSuccessAsync(string repoPath, long nextSnapshotVersion, CancellationToken cancellationToken)
    {
        var snapshot = await TryGetSnapshotAsync(repoPath, nextSnapshotVersion, cancellationToken);
        return new GitMutationResult { Success = true, Snapshot = snapshot };
    }

    private async Task<GitChangeSnapshot?> TryGetSnapshotAsync(string repoPath, long snapshotVersion, CancellationToken cancellationToken)
    {
        var result = await GetStatusAsync(repoPath, snapshotVersion, cancellationToken);
        return result.Success ? result.Snapshot : null;
    }

    private static IReadOnlyList<string> ValidateAndNormalizePaths(string repoPath, IReadOnlyList<string>? paths, out string? error)
    {
        error = null;
        if (paths == null || paths.Count == 0)
        {
            return [];
        }

        var normalized = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            var validation = GitRepositoryPathValidator.Validate(repoPath, path);
            if (!validation.IsValid)
            {
                error = validation.ErrorMessage;
                return [];
            }

            normalized.Add(validation.NormalizedRelativePath!);
        }

        return normalized;
    }

    private async Task<(bool IsMerging, bool IsRebasing, bool IsCherryPicking)> GetOperationStateAsync(string repoPath, CancellationToken cancellationToken)
    {
        var gitDir = await ResolveGitDirAsync(repoPath, cancellationToken);
        if (gitDir == null)
        {
            return (false, false, false);
        }

        var isMerging = File.Exists(Path.Combine(gitDir, "MERGE_HEAD"));
        var isCherryPicking = File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"));
        var isRebasing = Directory.Exists(Path.Combine(gitDir, "rebase-merge")) || Directory.Exists(Path.Combine(gitDir, "rebase-apply"));

        return (isMerging, isRebasing, isCherryPicking);
    }

    private async Task<string?> ResolveGitDirAsync(string repoPath, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await runner.RunAsync("git", ["rev-parse", "--git-dir"], repoPath, null, cancellationToken);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        var gitDir = stdout.Trim();
        return Path.IsPathRooted(gitDir) ? gitDir : Path.GetFullPath(Path.Combine(repoPath, gitDir));
    }

    private async Task<bool> IsBinaryAsync(string repoPath, string relativePath, GitDiffComparison comparison, CancellationToken cancellationToken)
    {
        string[] args = comparison == GitDiffComparison.Staged
            ? ["diff", "--cached", "--numstat", "--", relativePath]
            : ["diff", "--numstat", "--", relativePath];

        var (exitCode, stdout, _) = await runner.RunAsync("git", args, repoPath, null, cancellationToken);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return false;
        }

        // git numstat reports "-\t-\t<path>" for binary files.
        return stdout.TrimStart().StartsWith("-\t-\t", StringComparison.Ordinal);
    }

    private async Task<(long? OriginalSize, long? ModifiedSize)> GetBinarySizesAsync(
        string repoPath,
        string relativePath,
        GitDiffComparison comparison,
        string fullPath,
        CancellationToken cancellationToken)
    {
        long? originalSize = comparison == GitDiffComparison.Staged
            ? await GetBlobSizeAsync(repoPath, $"HEAD:{relativePath}", cancellationToken)
            : await GetBlobSizeAsync(repoPath, $":0:{relativePath}", cancellationToken);

        long? modifiedSize = comparison == GitDiffComparison.Staged
            ? await GetBlobSizeAsync(repoPath, $":0:{relativePath}", cancellationToken)
            : (File.Exists(fullPath) ? new FileInfo(fullPath).Length : null);

        return (originalSize, modifiedSize);
    }

    private async Task<long?> GetBlobSizeAsync(string repoPath, string objectSpec, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await runner.RunAsync("git", ["cat-file", "-s", objectSpec], repoPath, null, cancellationToken);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        return long.TryParse(stdout.Trim(), out var size) ? size : null;
    }

    private async Task<string?> ShowIndexContentAsync(string repoPath, string relativePath, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await runner.RunAsync("git", ["show", $":0:{relativePath}"], repoPath, null, cancellationToken);
        return exitCode == 0 ? stdout ?? string.Empty : null;
    }

    private async Task<string?> ShowRefContentAsync(string repoPath, string refName, string relativePath, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await runner.RunAsync("git", ["show", $"{refName}:{relativePath}"], repoPath, null, cancellationToken);
        return exitCode == 0 ? stdout ?? string.Empty : null;
    }

    private static async Task<string?> ReadWorkingTreeContentAsync(string fullPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(fullPath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(fullPath, cancellationToken);
    }

    private static bool ContainsBinaryMarker(string? content) => content != null && content.Contains('\0');

    private async Task<string?> GetHeadCommitShaAsync(string repoPath, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await runner.RunAsync("git", ["rev-parse", "HEAD"], repoPath, null, cancellationToken);
        return exitCode == 0 ? stdout?.Trim() : null;
    }

    private static string NormalizeCommitMessage(string commitMessage) =>
        commitMessage.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
}
