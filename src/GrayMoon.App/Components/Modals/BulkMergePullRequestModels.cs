namespace GrayMoon.App.Components.Modals;

/// <summary>Merge method choices offered by <see cref="BulkMergePullRequestsModal"/>'s toolbar picker. Unlike the single-PR dialog's per-method list (limited to what the repository actually allows), this applies uniformly across every selected row, so <see cref="RepositoryDefault"/> is offered alongside the three concrete methods rather than only the ones a specific repository permits.</summary>
public enum BulkMergeMethodSelection
{
    Squash,
    Merge,
    Rebase,
    RepositoryDefault
}

public static class BulkMergeMethodSelectionExtensions
{
    public static string ToDisplayLabel(this BulkMergeMethodSelection method) => method switch
    {
        BulkMergeMethodSelection.Squash => "Squash",
        BulkMergeMethodSelection.Merge => "Merge commit",
        BulkMergeMethodSelection.Rebase => "Rebase and merge",
        _ => "Repo default"
    };

    /// <summary>Phrase for the "Merge N pull requests using ___?" confirmation - a noun phrase, unlike <see cref="ToDisplayLabel"/>'s button-label wording, so it reads naturally after "using".</summary>
    public static string ToConfirmationPhrase(this BulkMergeMethodSelection method) => method switch
    {
        BulkMergeMethodSelection.Squash => "the Squash method",
        BulkMergeMethodSelection.Merge => "a merge commit",
        BulkMergeMethodSelection.Rebase => "Rebase and merge",
        _ => "each repository's own default method"
    };
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
    /// <summary>Local-clone git state - uncommitted changes / unpushed / incoming commits. GrayMoon-local and informational only (GitHub's own mergeability is unaffected), but must still be surfaced so a row never reads "ready to merge" while the local working copy is out of sync.</summary>
    public bool HasLocalWarning { get; set; }
    public int UncommittedChangesCount { get; set; }
    public int UnpushedCommitsCount { get; set; }
    public int IncomingCommitsCount { get; set; }
    public bool IsSelected { get; set; }
    public BulkMergeRowStatus Status { get; set; } = BulkMergeRowStatus.LoadingSnapshot;
    public string? ErrorMessage { get; set; }
}
