namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Explicit workspace commit operation. Never re-infer from mutable <c>_view</c> state at click
/// time - capture the mode when the command is rendered/chosen and pass that value through.
/// </summary>
public enum WorkspaceCommitMode
{
    /// <summary>Stage everything (<c>git add --all</c>) then commit.</summary>
    All,

    /// <summary>Commit only what is already staged; do not run <c>git add --all</c>.</summary>
    Staged,
}

/// <summary>
/// Pure helpers for workspace commit mode resolution. Kept free of Blazor page state so the
/// Commit All / Commit Staged contract is unit-testable without a circuit.
/// </summary>
public static class WorkspaceCommitModeHelper
{
    /// <summary>
    /// Which primary command the UI should offer/emphasize given current staged presence.
    /// Influences presentation only - never mutate a mode the user already selected.
    /// </summary>
    public static WorkspaceCommitMode ResolveOfferedMode(bool anyRepositoryHasStaged) =>
        anyRepositoryHasStaged ? WorkspaceCommitMode.Staged : WorkspaceCommitMode.All;

    public static bool StageAllFirst(WorkspaceCommitMode mode) => mode == WorkspaceCommitMode.All;

    public static string ButtonTitle(WorkspaceCommitMode mode) => mode switch
    {
        WorkspaceCommitMode.Staged => "Commit Staged",
        _ => "Commit All",
    };

    /// <summary>
    /// Whether a repository participates in a workspace commit for the given mode.
    /// Mode is per-operation: staged content in repository A must not flip repository B's
    /// StageAllFirst - B is either included under the chosen mode or skipped.
    /// </summary>
    public static bool IsRepositoryTarget(WorkspaceCommitMode mode, int stagedCount, int changedCount) =>
        mode == WorkspaceCommitMode.Staged
            ? stagedCount > 0
            : stagedCount > 0 || changedCount > 0;
}
