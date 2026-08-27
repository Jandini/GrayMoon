using GrayMoon.App.Services.GitChanges;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Components.GitChanges;

/// <summary>
/// <paramref name="RowKey"/> is the clicked row's tree key (<see cref="GrayMoon.App.Services.GitChanges.GitChangesTreeRow.Key"/>).
/// It lets the page scope the "in flight" spinner to just that single row instead of every row in the
/// affected repository (or, for folder scope, its descendant rows).
/// </summary>
public sealed record GitChangesStageEventArgs(int WorkspaceRepositoryId, GitChangeOperationScope Scope, IReadOnlyList<string> Paths, string RowKey);

/// <summary>
/// Raised when a File row's label is clicked. <paramref name="CtrlKey"/> and <paramref name="ShiftKey"/>
/// carry the click's modifier keys so the page can toggle/range-select the multi-selection instead of
/// always replacing it with a single selection.
/// </summary>
public sealed record GitChangesFileSelectEventArgs(GitChangesTreeRow Row, bool CtrlKey, bool ShiftKey);
