using GrayMoon.Common.Git;

namespace GrayMoon.App.Services.GitChanges;

public sealed record WorkspaceGitChangesView
{
    public required int WorkspaceId { get; init; }
    public required IReadOnlyList<WorkspaceGitChangesRepositoryView> Repositories { get; init; }
}

public sealed record WorkspaceGitChangesRepositoryView
{
    public required int WorkspaceRepositoryId { get; init; }
    public required int RepositoryId { get; init; }
    public required string RepositoryName { get; init; }

    public string? BranchName { get; init; }
    public string? DefaultBranchName { get; init; }
    public string? HeadCommit { get; init; }

    public bool IsDetachedHead { get; init; }
    public bool IsUnbornBranch { get; init; }
    public bool IsMerging { get; init; }
    public bool IsRebasing { get; init; }
    public bool IsCherryPicking { get; init; }

    public int StagedCount { get; init; }
    public int ChangedCount { get; init; }
    public int ConflictCount { get; init; }

    /// <summary>Persisted unstaged insertions. Null until a user-visible scan computed them.</summary>
    public int? Insertions { get; init; }

    /// <summary>Persisted unstaged deletions. Null until a user-visible scan computed them.</summary>
    public int? Deletions { get; init; }

    /// <summary>Persisted staged insertions. Null until a user-visible scan computed them.</summary>
    public int? StagedInsertions { get; init; }

    /// <summary>Persisted staged deletions. Null until a user-visible scan computed them.</summary>
    public int? StagedDeletions { get; init; }

    public DateTimeOffset? WorkerScannedAt { get; init; }
    public DateTimeOffset? PersistedAt { get; init; }

    /// <summary>Persisted content digest of this repository's snapshot; null on rows that predate the digest.</summary>
    public string? ContentDigest { get; init; }

    public string? LastErrorCode { get; init; }
    public string? LastErrorMessage { get; init; }

    public required IReadOnlyList<WorkspaceGitChangeEntryView> Changes { get; init; }

    /// <summary>
    /// Per-kind counts of unstaged (Changed) entries, classified like the header chips (conflicted counts as
    /// Unmerged, untracked as Added). Set when <see cref="Changes"/> is intentionally empty (large-mode
    /// summaries) so the header chips stay accurate; null when <see cref="Changes"/> is populated.
    /// </summary>
    public IReadOnlyDictionary<GitChangeKind, int>? ChangedKindCounts { get; init; }
}

public sealed record WorkspaceGitChangeEntryView
{
    public required string Path { get; init; }
    public string? OriginalPath { get; init; }
    public GitChangeKind IndexChange { get; init; }
    public GitChangeKind WorktreeChange { get; init; }
    public bool IsTracked { get; init; }
    public bool IsConflicted { get; init; }
    public bool IsSubmodule { get; init; }

    public bool IsStaged => IndexChange != GitChangeKind.None;
    public bool IsChanged => WorktreeChange != GitChangeKind.None;
}
