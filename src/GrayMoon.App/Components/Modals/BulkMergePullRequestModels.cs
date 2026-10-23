namespace GrayMoon.App.Components.Modals;

/// <summary>
/// Scope for the bulk-merge dialog's "Return to default branch" option.
/// <see cref="Merged"/> keeps today's behaviour (only repositories whose PR just merged).
/// <see cref="AllRepositories"/> also returns every other repository in the same merge scope that had no open PR
/// (typically because it had no changes on the branch).
/// </summary>
public enum BulkMergeReturnToDefaultScope
{
    Merged = 0,
    AllRepositories = 1
}

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
    /// <summary>Snapshot loaded, but GitHub's own mergeable computation hasn't resolved yet (Mergeable is still null / MergeableState is "unknown") - distinct from Ready/Conflict so the row never reads as mergeable, and from LoadingSnapshot so a pulsing title/subtitle placeholder isn't shown for data that's already loaded.</summary>
    CheckingMergeability,
    Ready,
    Conflict,
    Merging,
    Merged,
    Failed,
    ReturningToDefault,
    ReturnedToDefault,
    ReturnFailed,
    Skipped,
    /// <summary>Closed without merging via the drill-in single-PR dialog - distinct from <see cref="Skipped"/>, which means the batch run left this row unattempted (e.g. Abort was pressed).</summary>
    Closed
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
    /// <summary>
    /// Companion row for a repository in the merge scope that had no open PR. Only added when returning with
    /// <see cref="BulkMergeReturnToDefaultScope.AllRepositories"/>; never merged, only returned to default.
    /// </summary>
    public bool HasNoOpenPullRequest { get; init; }
}

/// <summary>
/// Applies one unattended return batch onto bulk-merge rows. Cancelled repositories that were not mutated
/// stay <see cref="BulkMergeRowStatus.ReturningToDefault"/> so the run finalizer can mark them cancelled.
/// </summary>
internal static class BulkMergeReturnStatus
{
    public static void Apply(
        IReadOnlyList<BulkMergePrRow> rows,
        IReadOnlyList<UnattendedReturnToDefaultRepositoryResult> results,
        Action<int> clearRepositoryError,
        Action<int, string> setRepositoryError)
    {
        var byId = new Dictionary<int, UnattendedReturnToDefaultRepositoryResult>(results.Count);
        foreach (var result in results)
            byId[result.RepositoryId] = result;

        foreach (var row in rows)
        {
            if (!byId.TryGetValue(row.RepositoryId, out var result))
                continue;

            if (result.Completed)
            {
                row.Status = BulkMergeRowStatus.ReturnedToDefault;
                row.ErrorMessage = null;
                clearRepositoryError(row.RepositoryId);
                continue;
            }

            if (result.FailureStage == UnattendedReturnFailureStage.Cancelled && !result.MutationCompleted)
                continue;

            row.Status = BulkMergeRowStatus.ReturnFailed;
            row.ErrorMessage = result.AbortReason ?? "Failed to return to default branch.";
            setRepositoryError(row.RepositoryId, row.ErrorMessage);
        }
    }
}

/// <summary>Terminal-row updates and counts for a finished bulk-merge run, including Abort.</summary>
internal static class BulkMergeRunFinalizer
{
    public static void FinalizeInterruptedRows(IEnumerable<BulkMergePrRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.Status == BulkMergeRowStatus.Merging)
            {
                row.Status = BulkMergeRowStatus.Skipped;
            }
            else if (row.Status == BulkMergeRowStatus.ReturningToDefault)
            {
                row.Status = BulkMergeRowStatus.ReturnFailed;
                row.ErrorMessage = UnattendedReturnToDefaultBatchResult.CancelledMessage;
            }
        }
    }

    public readonly record struct Counts(int Failed, int ReturnFailed, int Skipped, int Succeeded, int RealReturnFailed);

    public static Counts Count(IEnumerable<BulkMergePrRow> rows)
    {
        var list = rows as IReadOnlyList<BulkMergePrRow> ?? rows.ToList();
        return new Counts(
            list.Count(row => row.Status == BulkMergeRowStatus.Failed),
            list.Count(row => row.Status == BulkMergeRowStatus.ReturnFailed),
            list.Count(row => row.Status == BulkMergeRowStatus.Skipped),
            // Exclude no-open-PR companion rows: they were never merged, only returned to default.
            list.Count(row =>
                !row.HasNoOpenPullRequest &&
                row.Status is BulkMergeRowStatus.Merged or BulkMergeRowStatus.ReturnedToDefault),
            list.Count(row =>
                row.Status == BulkMergeRowStatus.ReturnFailed &&
                !string.Equals(row.ErrorMessage, UnattendedReturnToDefaultBatchResult.CancelledMessage, StringComparison.Ordinal)));
    }
}
