namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Layer A open payload: repository chrome and counts without path inventories.
/// Used when <c>FullHydrationMaxEntries</c> is exceeded so the circuit does not materialize every entry on open.
/// </summary>
public sealed record WorkspaceGitChangesSummariesView
{
    public required int WorkspaceId { get; init; }

    /// <summary>Distinct path rows in Layer B for this context (not Staged+Changed, which double-count).</summary>
    public required int TotalEntryCount { get; init; }

    public required IReadOnlyList<WorkspaceGitChangesRepositorySummary> Repositories { get; init; }
}

/// <summary>One repository's summary fields without <see cref="WorkspaceGitChangesRepositoryView.Changes"/>.</summary>
public sealed record WorkspaceGitChangesRepositorySummary
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

    public int? Insertions { get; init; }
    public int? Deletions { get; init; }
    public int? StagedInsertions { get; init; }
    public int? StagedDeletions { get; init; }

    public DateTimeOffset? WorkerScannedAt { get; init; }
    public DateTimeOffset? PersistedAt { get; init; }

    public string? LastErrorCode { get; init; }
    public string? LastErrorMessage { get; init; }

    /// <summary>Persisted snapshot version for this repo+context; used to detect stale tree-page caches.</summary>
    public long SnapshotVersion { get; init; }
}
