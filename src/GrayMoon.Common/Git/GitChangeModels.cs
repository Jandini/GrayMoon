namespace GrayMoon.Common.Git;

/// <summary>Git status change kind for a single index or worktree column (porcelain v2 XY codes).</summary>
public enum GitChangeKind
{
    None,
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Unmerged,
    Untracked,
}

/// <summary>A single changed path from a git status scan. May carry both an index and a worktree change.</summary>
public sealed record GitChangeEntry
{
    public required string Path { get; init; }
    public string? OriginalPath { get; init; }

    public GitChangeKind IndexChange { get; init; }
    public GitChangeKind WorktreeChange { get; init; }

    public bool IsTracked { get; init; } = true;
    public bool IsConflicted { get; init; }
    public bool IsSubmodule { get; init; }

    public bool IsStaged => IndexChange != GitChangeKind.None;
    public bool IsChanged => WorktreeChange != GitChangeKind.None;
}

/// <summary>A versioned point-in-time git status scan for one repository.</summary>
public sealed record GitChangeSnapshot
{
    public required long Version { get; init; }

    public required string BranchName { get; init; }
    public string? HeadCommit { get; init; }

    public bool IsDetachedHead { get; init; }
    public bool IsUnbornBranch { get; init; }
    public bool IsMerging { get; init; }
    public bool IsRebasing { get; init; }
    public bool IsCherryPicking { get; init; }

    public required IReadOnlyList<GitChangeEntry> Changes { get; init; }
    public required DateTimeOffset ScannedAt { get; init; }
}

/// <summary>Explicit scope for a stage/unstage mutation - never inferred from currently rendered UI state.</summary>
public enum GitChangeOperationScope
{
    ExplicitPaths,
    Folder,
    Repository,
    MultipleRepositories,
    EntireSection,
}

/// <summary>Which two states a diff compares.</summary>
public enum GitDiffComparison
{
    /// <summary>HEAD -&gt; Index.</summary>
    Staged,

    /// <summary>Index -&gt; Working tree.</summary>
    Unstaged,
}

/// <summary>Special-case content state a diff viewer must render without attempting a normal text diff.</summary>
public enum GitDiffContentState
{
    Normal,
    NewFile,
    DeletedFile,
    Binary,
    TooLarge,
    UnsupportedEncoding,
    Error,
}

/// <summary>Original/modified content pair for one file's diff, plus enough metadata to render special states safely.</summary>
public sealed record GitDiffDocument
{
    public required string Path { get; init; }
    public string? OriginalPath { get; init; }
    public required GitDiffComparison Comparison { get; init; }
    public required GitDiffContentState State { get; init; }

    public string? OriginalContent { get; init; }
    public string? ModifiedContent { get; init; }
    public long? OriginalSizeBytes { get; init; }
    public long? ModifiedSizeBytes { get; init; }

    public string? LanguageId { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>Result of a stage/unstage mutation. Always carries the post-operation snapshot when successful.</summary>
public sealed record GitMutationResult
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public GitChangeSnapshot? Snapshot { get; init; }
}

/// <summary>Result of a commit operation for a single repository.</summary>
public sealed record GitCommitResult
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? CommitSha { get; init; }
    public GitChangeSnapshot? Snapshot { get; init; }
}
