namespace GrayMoon.App.Components.Modals;

/// <summary>Merge method choices offered by <see cref="BulkMergePullRequestsModal"/>'s toolbar picker. Unlike the single-PR dialog's per-method list (limited to what the repository actually allows), this applies uniformly across every selected row, so <see cref="RepositoryDefault"/> is offered alongside the three concrete methods rather than only the ones a specific repository permits.</summary>
public enum BulkMergeMethodSelection
{
    Squash,
    Merge,
    Rebase,
    RepositoryDefault
}

public enum BulkMergeRowStatus
{
    LoadingSnapshot,
    Ready,
    Conflict,
    Merging,
    Merged,
    Failed,
    SyncingToDefault,
    Synced,
    SyncFailed,
    Skipped
}

/// <summary>
/// One row in <see cref="BulkMergePullRequestsModal"/>. A mutable class, not a record, because rows are
/// updated live as the batch runs (snapshot load, then merge/sync progress) - per CLAUDE.md's exception for
/// modal state that mutates mid-display. Mutated only from the page's SafeInvoke/InvokeAsync callbacks.
/// </summary>
public sealed class BulkMergePrRow
{
    public required int RepositoryId { get; init; }
    public required string RepositoryName { get; init; }
    public required int PrNumber { get; init; }
    public string? PrHtmlUrl { get; set; }
    public string? Title { get; set; }
    public string? HeadSha { get; set; }
    public bool? Mergeable { get; set; }
    public string? MergeableState { get; set; }
    public bool IsSelected { get; set; }
    public BulkMergeRowStatus Status { get; set; } = BulkMergeRowStatus.LoadingSnapshot;
    public string? ErrorMessage { get; set; }
}
