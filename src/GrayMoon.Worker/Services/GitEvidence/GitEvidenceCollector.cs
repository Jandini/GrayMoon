using System.Text;
using GrayMoon.Common;
using GrayMoon.Common.Ai;
using GrayMoon.Common.Git;

namespace GrayMoon.Worker.Services.GitEvidence;

/// <summary>
/// Collects bounded, read-only Git evidence for one repository. Cheap first: one inventory diff
/// (<c>--raw --numstat -z</c>) says which files changed and how much; files are classified and ranked before any patch is
/// read; then one diff limited to the chosen paths reads hunks, stopped at a byte cap, plus a few capped single-file
/// reads for large high-value files. Nothing here writes the index, the working tree or refs, and nothing fetches.
/// </summary>
public sealed class GitEvidenceCollector(IBoundedProcessRunner runner)
{
    /// <summary>The empty tree: the base of a repository whose branch has no commit yet.</summary>
    internal const string EmptyTreeOid = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private const int MaxPatchFiles = 16;
    private const int LargeFileChurn = 1_000;
    private const int MaxLargeFiles = 3;
    private const int MaxUntrackedStatFiles = 50;
    private const int MaxUntrackedStatBytes = 262_144;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    // Read-only, deterministic output: raw UTF-8 paths, no color, no external diff or textconv programs, no optional locks.
    private static readonly string[] GitPrefix = ["-c", "core.quotepath=false", "-c", "color.ui=never", "-c", "diff.renames=true", "--no-optional-locks"];
    private static readonly string[] DiffFlags = ["--no-ext-diff", "--no-textconv", "-M"];

    public async Task<RepositoryGitEvidence> CollectCommitAsync(
        string repositoryPath,
        GitEvidenceRepositoryScope scope,
        GitCommitEvidenceMode mode,
        GitEvidenceLimits limits,
        CancellationToken cancellationToken)
    {
        var evidence = NewEvidence(scope);

        // Independent reads run together: HEAD with recent subjects, and (Commit All) the untracked listing.
        var logTask = GitAsync(repositoryPath, ["log", $"-n{Math.Max(1, limits.MaxRecentSubjects)}", "--format=%H%x1f%s", "-z", "HEAD"], 65_536, cancellationToken);
        var untrackedTask = mode == GitCommitEvidenceMode.All
            ? CollectUntrackedAsync(repositoryPath, cancellationToken)
            : Task.FromResult(new List<GitEvidenceFile>());
        var log = await logTask;
        if (log.ExitCode == 0)
        {
            var records = SplitNul(log.StdoutText);
            for (var i = 0; i < records.Count; i++)
            {
                var separator = records[i].IndexOf('\x1f');
                if (separator < 0)
                    continue;
                if (i == 0)
                    evidence.HeadOid = records[i][..separator].Trim();
                evidence.RecentSubjects.Add(records[i][(separator + 1)..]);
            }
        }
        else
        {
            var verify = await GitAsync(repositoryPath, ["rev-parse", "--verify", "-q", "HEAD"], 4_096, cancellationToken);
            if (verify.ExitCode == 0)
            {
                await untrackedTask;
                return Fail(evidence, GitEvidenceErrorCodes.GitFailed, FirstLine(log.Stderr, "git log failed."));
            }

            evidence.IsUnbornHead = true;
        }

        var baseTree = evidence.IsUnbornHead ? EmptyTreeOid : "HEAD";
        // Staged compares HEAD with the index only; All compares HEAD with the final working tree, which already
        // includes staged content, so staged and unstaged patches are never added up as separate changes.
        string[] range = mode == GitCommitEvidenceMode.Staged ? ["--cached", baseTree] : [baseTree];

        var inventory = await GitAsync(repositoryPath, ["diff", .. range, "--raw", "--numstat", "-z", .. DiffFlags], limits.MaxRawDiffBytes, cancellationToken);
        var untracked = await untrackedTask;
        if (inventory.ExitCode != 0 && !inventory.StdoutTruncated)
            return Fail(evidence, GitEvidenceErrorCodes.GitFailed, FirstLine(inventory.Stderr, "git diff failed."));

        var files = GitDiffInventoryParser.Parse(inventory.StdoutText).Select(ToFile).ToList();
        files.AddRange(untracked);

        ApplyInventory(evidence, files, limits);
        evidence.PatchReadTruncated |= inventory.StdoutTruncated;

        await ReadPatchesAsync(repositoryPath, range, evidence, limits, cancellationToken);
        return evidence;
    }

    public async Task<RepositoryGitEvidence> CollectPullRequestAsync(
        string repositoryPath,
        GitEvidenceRepositoryScope scope,
        GitEvidenceLimits limits,
        CancellationToken cancellationToken)
    {
        var evidence = NewEvidence(scope);
        var head = scope.HeadBranch?.Trim();
        var target = scope.BaseBranch?.Trim();
        if (!IsSafeBranchName(head) || !IsSafeBranchName(target))
            return Fail(evidence, GitEvidenceErrorCodes.InvalidBranchName, "The source or target branch name is not valid.");

        evidence.HeadRef = head;
        evidence.BaseRef = $"origin/{target}";

        // Full refs: a tag or another ref with the same short name can never be picked by accident. The target is the
        // remote-tracking branch, the same branch GitHub compares the pull request against.
        var headTask = RevParseAsync(repositoryPath, $"refs/heads/{head}^{{commit}}", cancellationToken);
        var baseTask = RevParseAsync(repositoryPath, $"refs/remotes/origin/{target}^{{commit}}", cancellationToken);
        evidence.HeadOid = await headTask;
        evidence.BaseOid = await baseTask;
        if (evidence.HeadOid is null)
            return Fail(evidence, GitEvidenceErrorCodes.SourceBranchNotFound, $"The source branch {head} was not found in this repository.");
        if (evidence.BaseOid is null)
            return Fail(evidence, GitEvidenceErrorCodes.TargetBranchNotFound,
                $"The target branch origin/{target} is not in the local repository. Refresh the branches and try again.");

        var mergeBase = await GitAsync(repositoryPath, ["merge-base", evidence.BaseOid, evidence.HeadOid], 4_096, cancellationToken);
        evidence.MergeBaseOid = mergeBase.ExitCode == 0 ? mergeBase.StdoutText.Trim() : null;
        if (string.IsNullOrEmpty(evidence.MergeBaseOid))
        {
            var shallow = await GitAsync(repositoryPath, ["rev-parse", "--is-shallow-repository"], 1_024, cancellationToken);
            var hint = shallow.StdoutText.Trim() == "true" ? " The repository is a shallow clone, so the shared history may be missing." : string.Empty;
            return Fail(evidence, GitEvidenceErrorCodes.NoMergeBase, $"{head} and origin/{target} have no common history.{hint}");
        }

        var commitRange = $"{evidence.BaseOid}..{evidence.HeadOid}";
        var countTask = GitAsync(repositoryPath, ["rev-list", "--count", commitRange], 1_024, cancellationToken);
        var subjectsTask = GitAsync(repositoryPath, ["log", "--no-merges", "--format=%s", "-z", $"-n{Math.Max(1, limits.MaxCommitSubjects)}", commitRange], 65_536, cancellationToken);
        // Three-dot semantics, as on GitHub: the source tip against the merge base, so commits that are only on the
        // target (or older parent history) never show up as part of this change.
        string[] range = [evidence.MergeBaseOid, evidence.HeadOid];
        var inventoryTask = GitAsync(repositoryPath, ["diff", .. range, "--raw", "--numstat", "-z", .. DiffFlags], limits.MaxRawDiffBytes, cancellationToken);

        var count = await countTask;
        evidence.CommitCount = int.TryParse(count.StdoutText.Trim(), out var commits) ? commits : 0;
        if (evidence.CommitCount == 0)
            return Fail(evidence, GitEvidenceErrorCodes.NoCommitsAhead, $"{head} has no commits that origin/{target} does not have.");

        var subjects = await subjectsTask;
        evidence.CommitSubjects.AddRange(SplitNul(subjects.StdoutText).Select(s => s.Trim()).Where(s => s.Length > 0));

        var inventory = await inventoryTask;
        if (inventory.ExitCode != 0 && !inventory.StdoutTruncated)
            return Fail(evidence, GitEvidenceErrorCodes.GitFailed, FirstLine(inventory.Stderr, "git diff failed."));

        ApplyInventory(evidence, GitDiffInventoryParser.Parse(inventory.StdoutText).Select(ToFile).ToList(), limits);
        evidence.PatchReadTruncated |= inventory.StdoutTruncated;

        await ReadPatchesAsync(repositoryPath, range, evidence, limits, cancellationToken);
        return evidence;
    }

    /// <summary>
    /// A short branch name that is safe as part of a full ref and as a git argument: never an option, never a revision
    /// expression. Stricter than <c>git check-ref-format</c> on purpose.
    /// </summary>
    internal static bool IsSafeBranchName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255)
            return false;
        if (name[0] is '-' or '/' || name.EndsWith('/') || name.EndsWith('.') || name.EndsWith(".lock", StringComparison.Ordinal))
            return false;
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains("@{", StringComparison.Ordinal) || name.Contains("//", StringComparison.Ordinal))
            return false;
        foreach (var c in name)
        {
            if (char.IsControl(c) || c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\')
                return false;
        }

        return true;
    }

    private static RepositoryGitEvidence NewEvidence(GitEvidenceRepositoryScope scope) => new()
    {
        RepositoryId = scope.RepositoryId,
        RepositoryName = scope.RepositoryName ?? string.Empty,
    };

    private static RepositoryGitEvidence Fail(RepositoryGitEvidence evidence, string code, string message)
    {
        evidence.Success = false;
        evidence.ErrorCode = code;
        evidence.ErrorMessage = message;
        return evidence;
    }

    private static GitEvidenceFile ToFile(GitDiffInventoryEntry entry) => new()
    {
        Path = entry.Path,
        OldPath = entry.OldPath,
        Change = entry.Change,
        Added = entry.Added,
        Deleted = entry.Deleted,
        IsBinary = entry.IsBinary,
        IsSubmodule = entry.IsSubmodule,
        IsSymlink = entry.IsSymlink,
        Category = GitEvidenceClassifier.Classify(entry.Path),
        IsSensitive = GitEvidenceClassifier.IsSensitive(entry.Path),
    };

    private static void ApplyInventory(RepositoryGitEvidence evidence, List<GitEvidenceFile> files, GitEvidenceLimits limits)
    {
        evidence.TotalFiles = files.Count;
        evidence.TotalAdded = files.Sum(f => f.Added ?? 0);
        evidence.TotalDeleted = files.Sum(f => f.Deleted ?? 0);
        evidence.ConflictedPaths = files.Where(f => f.Change == GitEvidenceChange.Unmerged).Select(f => f.Path).ToList();

        var ranked = GitEvidenceClassifier.Rank(files).ToList();
        evidence.Files = ranked.Take(Math.Max(0, limits.MaxInventoryFiles)).ToList();
        evidence.OmittedFilesByCategory = ranked.Skip(evidence.Files.Count)
            .GroupBy(f => f.Category)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Untracked, not ignored files (what <c>git add --all</c> would add). Paths come from git and are validated before
    /// anything is read; line counts are read for the most relevant ones only, so a new folder of thousands of files costs
    /// a listing, not thousands of reads. Links are never followed.
    /// </summary>
    private async Task<List<GitEvidenceFile>> CollectUntrackedAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var listing = await GitAsync(repositoryPath, ["ls-files", "--others", "--exclude-standard", "-z"], 1_048_576, cancellationToken);
        var files = SplitNul(listing.StdoutText)
            .Where(p => p.Length > 0 && !p.EndsWith('/'))
            .Select(path => new GitEvidenceFile
            {
                Path = path,
                Change = GitEvidenceChange.Added,
                IsUntracked = true,
                Category = GitEvidenceClassifier.Classify(path),
                IsSensitive = GitEvidenceClassifier.IsSensitive(path),
            })
            .ToList();

        foreach (var file in files.OrderByDescending(f => GitEvidenceClassifier.Weight(f.Category)).ThenBy(f => f.Path, StringComparer.Ordinal).Take(MaxUntrackedStatFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = ResolveUntracked(repositoryPath, file.Path);
            if (info is null)
                continue;
            if (info.LinkTarget is not null)
            {
                file.IsSymlink = true;
                continue;
            }

            if (file.IsSensitive || info.Length > MaxUntrackedStatBytes)
                continue;

            var bytes = await File.ReadAllBytesAsync(info.FullName, cancellationToken);
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8_192)) >= 0)
            {
                file.IsBinary = true;
                continue;
            }

            file.Added = CountLines(bytes);
            file.Deleted = 0;
        }

        return files;
    }

    private async Task ReadPatchesAsync(string repositoryPath, string[] range, RepositoryGitEvidence evidence, GitEvidenceLimits limits, CancellationToken cancellationToken)
    {
        var perFile = Math.Max(256, limits.MaxPatchBytesPerFile);
        var budget = Math.Max(perFile, limits.MaxPatchBytesPerRepository);

        // Choose before reading: ranked files that can say something, until the expected patch size covers the budget
        // with some headroom for trimming at hunk boundaries.
        var chosen = new List<GitEvidenceFile>();
        var expected = 0;
        foreach (var file in evidence.Files)
        {
            if (chosen.Count >= MaxPatchFiles || expected >= budget * 2)
                break;
            if (GitEvidenceClassifier.Score(file) <= 0 || file.Churn == 0)
                continue;
            chosen.Add(file);
            expected += Math.Min(file.Churn * 50, perFile);
        }

        var untracked = chosen.Where(f => f.IsUntracked).ToList();
        var large = chosen.Where(f => !f.IsUntracked && f.Churn > LargeFileChurn).Take(MaxLargeFiles).ToList();
        var regular = chosen.Where(f => !f.IsUntracked && f.Churn <= LargeFileChurn).ToList();

        // At most 1 + MaxLargeFiles git processes and a few small file reads for this repository, all at once; each
        // fills only its own files, so they never race on the same evidence entry.
        var reads = new List<Task>();
        if (regular.Count > 0)
        {
            reads.Add(Task.Run(async () =>
            {
                var paths = regular.SelectMany(f => f.OldPath is null ? new[] { f.Path } : [f.OldPath, f.Path]).Distinct(StringComparer.Ordinal);
                var diff = await GitAsync(repositoryPath,
                    ["diff", .. range, "-U3", "--no-color", .. DiffFlags, "--", .. paths.Select(p => ":(literal)" + p)],
                    limits.MaxRawDiffBytes, cancellationToken);
                if (diff.StdoutTruncated)
                    evidence.PatchReadTruncated = true;
                ApplyPatches(regular, diff.StdoutText, perFile, partialRead: diff.StdoutTruncated);
            }, cancellationToken));
        }

        foreach (var file in large)
        {
            reads.Add(Task.Run(async () =>
            {
                // A big file: read only the beginning of its diff and stop the process there.
                string[] paths = file.OldPath is null ? [file.Path] : [file.OldPath, file.Path];
                var diff = await GitAsync(repositoryPath,
                    ["diff", .. range, "-U2", "--no-color", .. DiffFlags, "--", .. paths.Select(p => ":(literal)" + p)],
                    perFile * 4, cancellationToken);
                ApplyPatches([file], diff.StdoutText, perFile, partialRead: true);
            }, cancellationToken));
        }

        if (untracked.Count > 0)
        {
            reads.Add(Task.Run(async () =>
            {
                foreach (var file in untracked)
                {
                    var excerpt = await ReadUntrackedExcerptAsync(repositoryPath, file, Math.Min(perFile, limits.MaxUntrackedReadBytes), cancellationToken);
                    if (excerpt is null)
                        continue;
                    file.Patch = excerpt.Value.Text;
                    file.PatchTruncated = excerpt.Value.Truncated;
                }
            }, cancellationToken));
        }

        await Task.WhenAll(reads);
    }

    private static void ApplyPatches(IReadOnlyList<GitEvidenceFile> files, string diff, int perFile, bool partialRead)
    {
        var expected = files.ToDictionary(f => f.Path, f => f.OldPath, StringComparer.Ordinal);
        var sections = UnifiedDiffSplitter.Split(diff, expected);
        foreach (var file in files)
        {
            if (!sections.TryGetValue(file.Path, out var hunks))
                continue;
            file.Patch = UnifiedDiffSplitter.Trim(hunks, perFile, out var cut);
            file.PatchTruncated = cut || (partialRead && file.Churn > CountDiffLines(file.Patch));
        }
    }

    private static async Task<(string Text, bool Truncated)?> ReadUntrackedExcerptAsync(string repositoryPath, GitEvidenceFile file, int maxBytes, CancellationToken cancellationToken)
    {
        if (file.IsSensitive || file.IsBinary || file.IsSymlink)
            return null;
        var info = ResolveUntracked(repositoryPath, file.Path);
        if (info is null || info.LinkTarget is not null)
            return null;

        var buffer = new byte[Math.Min(maxBytes, (int)Math.Min(info.Length, int.MaxValue))];
        await using (var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4_096, useAsync: true))
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
                if (n == 0)
                    break;
                read += n;
            }

            Array.Resize(ref buffer, read);
        }

        var text = new UTF8Encoding(false, false).GetString(buffer).Replace("\r\n", "\n");
        var lines = text.Split('\n');
        var truncated = info.Length > buffer.Length;
        if (truncated && lines.Length > 1)
            lines = lines[..^1];
        var hunk = new StringBuilder($"@@ new file @@\n");
        foreach (var line in lines)
            hunk.Append('+').Append(line).Append('\n');
        var trimmed = UnifiedDiffSplitter.Trim(hunk.ToString(), maxBytes, out var cut);
        return (trimmed, truncated || cut);
    }

    private static FileInfo? ResolveUntracked(string repositoryPath, string relativePath)
    {
        var validation = GitRepositoryPathValidator.Validate(repositoryPath, relativePath);
        if (!validation.IsValid || validation.FullPath is null)
            return null;
        var info = new FileInfo(validation.FullPath);
        return info.Exists ? info : null;
    }

    private async Task<string?> RevParseAsync(string repositoryPath, string revision, CancellationToken cancellationToken)
    {
        var result = await GitAsync(repositoryPath, ["rev-parse", "--verify", "-q", revision], 4_096, cancellationToken);
        var oid = result.StdoutText.Trim();
        return result.ExitCode == 0 && oid.Length >= 40 ? oid : null;
    }

    private Task<BoundedProcessResult> GitAsync(string repositoryPath, string[] arguments, int maxStdoutBytes, CancellationToken cancellationToken) =>
        runner.RunAsync(new BoundedProcessRequest("git", [.. GitPrefix, .. arguments])
        {
            WorkingDirectory = repositoryPath,
            MaxStdoutBytes = maxStdoutBytes,
            Timeout = GitTimeout,
            Environment = new Dictionary<string, string?>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "never",
                ["GIT_OPTIONAL_LOCKS"] = "0",
                ["GIT_PAGER"] = "cat",
            },
        }, cancellationToken);

    private static List<string> SplitNul(string text) =>
        text.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static int CountLines(byte[] bytes)
    {
        if (bytes.Length == 0)
            return 0;
        var lines = bytes.Count(b => b == (byte)'\n');
        return bytes[^1] == (byte)'\n' ? lines : lines + 1;
    }

    private static int CountDiffLines(string? patch) =>
        patch is null ? 0 : patch.Split('\n').Count(l => l.Length > 0 && l[0] is '+' or '-');

    private static string FirstLine(string? text, string fallback)
    {
        var line = (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? fallback : line.Length > 300 ? line[..300] : line;
    }
}
