using GrayMoon.Common.Git;

namespace GrayMoon.App.Components.GitChanges;

/// <summary>
/// <paramref name="RowKey"/> is the clicked row's tree key (<see cref="GrayMoon.App.Services.GitChanges.GitChangesTreeRow.Key"/>).
/// It lets the page scope the "in flight" spinner to just that single row instead of every row in the
/// affected repository (or, for folder scope, its descendant rows).
/// </summary>
public sealed record GitChangesStageEventArgs(int WorkspaceRepositoryId, GitChangeOperationScope Scope, IReadOnlyList<string> Paths, string RowKey);
