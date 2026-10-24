using System.Security.Cryptography;
using System.Text;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Order-independent digest of everything a repository's Changes view shows: every entry (path, original path,
/// kinds, flags) plus the status fields (branch, head, state flags, counts, line stats). Two snapshots with the
/// same digest paint identically, so a live update carrying an unchanged digest is a no-op for the page.
/// </summary>
public static class GitChangesContentDigest
{
    /// <summary>Sentinel appended to the status segment so it can never collide with an entry line.</summary>
    private const char StatusMarker = '\u0001';

    public static string Compute(
        IEnumerable<GitChangeEntry> entries,
        string? branchName,
        string? headCommit,
        bool isDetachedHead,
        bool isUnbornBranch,
        bool isMerging,
        bool isRebasing,
        bool isCherryPicking,
        int stagedCount,
        int changedCount,
        int conflictCount,
        int? insertions,
        int? deletions,
        int? stagedInsertions,
        int? stagedDeletions)
    {
        // Sort by the full tuple text, ordinally, so input order never matters.
        var lines = entries
            .Select(e => string.Join(
                '\u0000',
                e.Path,
                e.OriginalPath ?? string.Empty,
                (int)e.IndexChange,
                (int)e.WorktreeChange,
                e.IsTracked ? 1 : 0,
                e.IsConflicted ? 1 : 0,
                e.IsSubmodule ? 1 : 0))
            .ToList();
        lines.Sort(StringComparer.Ordinal);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var line in lines)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(line));
            hash.AppendData("\n"u8);
        }

        var status = string.Join(
            '\u0000',
            StatusMarker,
            branchName ?? string.Empty,
            headCommit ?? string.Empty,
            isDetachedHead ? 1 : 0,
            isUnbornBranch ? 1 : 0,
            isMerging ? 1 : 0,
            isRebasing ? 1 : 0,
            isCherryPicking ? 1 : 0,
            stagedCount,
            changedCount,
            conflictCount,
            insertions?.ToString() ?? "-",
            deletions?.ToString() ?? "-",
            stagedInsertions?.ToString() ?? "-",
            stagedDeletions?.ToString() ?? "-");
        hash.AppendData(Encoding.UTF8.GetBytes(status));

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
