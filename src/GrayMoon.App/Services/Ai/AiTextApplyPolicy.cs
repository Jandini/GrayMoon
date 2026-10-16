using System.Globalization;
using GrayMoon.App.Services.GitChanges;

namespace GrayMoon.App.Services.Ai;

/// <summary>Whether a generated text goes straight into the field, or is offered as a suggestion (with a reason when there is a warning to give).</summary>
public sealed record AiApplyDecision(bool ApplyDirectly, string? Reason);

/// <summary>
/// Generated text never silently replaces the user's own words or describes a scope that moved on. It is applied
/// directly only when the field was empty when generation started, is still unchanged, and the scope is the one the
/// evidence was collected for. Otherwise it is a suggestion the user may Replace, Copy or Dismiss.
/// </summary>
public static class AiTextApplyPolicy
{
    public const string EditedReason = "You edited the text while it was being written.";
    public const string StaleChangesReason = "The changes moved on while it was being written; check it still fits.";
    public const string StaleBranchesReason = "A branch selection changed while it was being written; it describes the previous selection.";

    public static AiApplyDecision Decide(string textAtClick, string textNow, bool scopeChanged, string staleReason)
    {
        if (scopeChanged)
            return new AiApplyDecision(false, staleReason);
        if (!string.Equals(textAtClick, textNow, StringComparison.Ordinal))
            return new AiApplyDecision(false, EditedReason);
        if (!string.IsNullOrWhiteSpace(textAtClick))
            return new AiApplyDecision(false, null);
        return new AiApplyDecision(true, null);
    }

    /// <summary>
    /// Identity of a workspace commit's scope as the page sees it: per target repository its HEAD, its staged and
    /// changed counts and when the Worker last scanned it. Any watcher-driven rescan changes it, which is the signal
    /// that files may differ from what the evidence read.
    /// </summary>
    public static string CommitScopeFingerprint(WorkspaceCommitMode mode, IEnumerable<WorkspaceGitChangesRepositoryView> repositories) =>
        mode + "\n" + string.Join(
            "\n",
            repositories
                .Where(r => WorkspaceCommitModeHelper.IsRepositoryTarget(mode, r.StagedCount, r.ChangedCount))
                .OrderBy(r => r.RepositoryId)
                .Select(r => string.Join(
                    '\t',
                    r.RepositoryId.ToString(CultureInfo.InvariantCulture),
                    r.HeadCommit ?? "",
                    r.StagedCount.ToString(CultureInfo.InvariantCulture),
                    r.ChangedCount.ToString(CultureInfo.InvariantCulture),
                    r.WorkerScannedAt?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "")));
}
