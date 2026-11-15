using GrayMoon.App.Models;
using GrayMoon.App.Services.Policy;
using GrayMoon.Application.Repositories.Policy;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceActions
{
    internal enum ActionGroupKind
    {
        /// <summary>The whole grid as one list with no header (a workspace without a dependency graph).</summary>
        Flat,

        /// <summary>The Workspace repository, pinned above the level groups.</summary>
        Workspace,

        /// <summary>One dependency level; a null <see cref="ActionRowGroup.LevelKey"/> is "No dependencies".</summary>
        Level
    }

    internal sealed record ActionRowGroup(ActionGroupKind Kind, int? LevelKey, IReadOnlyList<WorkspaceActionRow> Rows);

    /// <summary>True when the workspace profile has a dependency graph, so the grid is grouped like the Repositories grid.</summary>
    private bool _groupByDependencyLevel;

    /// <summary>
    /// The dependency level of a repository in the selected context. A Feature has its own graph, so its level comes
    /// from that context's snapshot and never from the shared link, which holds the Workspace's level.
    /// </summary>
    internal static int? ResolveDependencyLevel(WorkspaceRepositoryLink link, bool isFeatureContext, int? contextDependencyLevel) =>
        isFeatureContext ? contextDependencyLevel : link.DependencyLevel;

    /// <summary>
    /// Splits rows into the groups the grid renders, in the order of the Repositories grid: the Workspace repository,
    /// then levels from the highest down, then repositories without a level. The order of rows inside a group is the
    /// order they arrive in.
    /// </summary>
    internal static IReadOnlyList<ActionRowGroup> BuildGroups(IEnumerable<WorkspaceActionRow> orderedRows, bool groupByDependencyLevel)
    {
        var all = orderedRows.ToList();
        if (!groupByDependencyLevel)
            return all.Count == 0 ? [] : [new ActionRowGroup(ActionGroupKind.Flat, null, all)];

        var groups = new List<ActionRowGroup>();

        var workspaceRows = all.Where(row => row.IsWorkspaceRepository).ToList();
        if (workspaceRows.Count > 0)
            groups.Add(new ActionRowGroup(ActionGroupKind.Workspace, null, workspaceRows));

        groups.AddRange(all
            .Where(row => !row.IsWorkspaceRepository)
            .GroupBy(row => row.DependencyLevel)
            .OrderByDescending(group => group.Key ?? int.MinValue)
            .Select(group => new ActionRowGroup(ActionGroupKind.Level, group.Key, group.ToList())));

        return groups;
    }

    /// <summary>What a group header shows: how many repositories the group has, and what its menu would act on.</summary>
    internal sealed record ActionGroupSummary(int RepositoryCount, int FailedCount, int RunningCount, int RunCount, int RerunCount);

    internal const string WorkspaceGroupTitle = "Workspace";

    /// <summary>Header and menu title of a group, as on the Repositories grid.</summary>
    internal static string GroupTitle(ActionRowGroup group) => group.Kind switch
    {
        ActionGroupKind.Workspace => WorkspaceGroupTitle,
        ActionGroupKind.Level when group.LevelKey.HasValue => $"Level {group.LevelKey}",
        ActionGroupKind.Level => "No dependencies",
        _ => string.Empty
    };

    /// <summary>
    /// A group gets a header only when the grid is grouped and at least one of its rows is visible; a group hidden by
    /// the status chips or the search renders nothing.
    /// </summary>
    internal static bool ShowsGroupHeader(ActionRowGroup group, Func<WorkspaceActionRow, bool> hasVisibleLines) =>
        group.Kind != ActionGroupKind.Flat && group.Rows.Any(hasVisibleLines);

    /// <summary>True when a bulk action may start a workflow in the row's repository (not read only, not on a tag).</summary>
    internal static bool AllowsBulkWorkflowRun(WorkspaceActionRow row, bool isFeatureContext) =>
        row.Link.Allows(RepositoryOperation.RunWorkflow, isFeatureContext, RepositoryOperationScope.Bulk);

    /// <summary>
    /// Workflows that Run starts: every dispatchable workflow that is not running or already being started. Status
    /// chips and search do not narrow it; the AI toggle does.
    /// </summary>
    internal static List<(WorkspaceActionRow Row, WorkflowActionLine Line)> SelectRunnable(
        IEnumerable<WorkspaceActionRow> rows, bool isFeatureContext, bool includeAiWorkflows) =>
        SelectLines(rows, isFeatureContext, includeAiWorkflows,
            (row, line) => CanRun(row, line) && !IsLineRunningForBranch(row, line) && !line.RunInProgress);

    /// <summary>Workflows that Re-run starts again: every failed workflow that has a run to re-run.</summary>
    internal static List<(WorkspaceActionRow Row, WorkflowActionLine Line)> SelectRerunnable(
        IEnumerable<WorkspaceActionRow> rows, bool isFeatureContext, bool includeAiWorkflows) =>
        SelectLines(rows, isFeatureContext, includeAiWorkflows, CanRerun);

    /// <summary>Failed workflows that can also be dispatched again on the latest commit of the branch.</summary>
    internal static List<(WorkspaceActionRow Row, WorkflowActionLine Line)> SelectRunAgain(
        IEnumerable<WorkspaceActionRow> rows, bool isFeatureContext, bool includeAiWorkflows) =>
        SelectLines(rows, isFeatureContext, includeAiWorkflows, CanRunAgain);

    private static List<(WorkspaceActionRow Row, WorkflowActionLine Line)> SelectLines(
        IEnumerable<WorkspaceActionRow> rows,
        bool isFeatureContext,
        bool includeAiWorkflows,
        Func<WorkspaceActionRow, WorkflowActionLine, bool> accepts) =>
        rows.Where(row => AllowsBulkWorkflowRun(row, isFeatureContext))
            .SelectMany(row => row.WorkflowLines
                .Where(line => IsLineIncludedWhenAiFiltered(line, includeAiWorkflows) && accepts(row, line))
                .Select(line => (row, line)))
            .ToList();

    private ActionGroupSummary SummarizeGroup(ActionRowGroup group) => new(
        group.Rows.Count,
        group.Rows.Sum(row => row.WorkflowLines.Count(line => CountsTowardStatus(row, line) && IsLineFailedForBranch(row, line))),
        group.Rows.Sum(row => row.WorkflowLines.Count(line => CountsTowardStatus(row, line) && IsLineRunningForBranch(row, line))),
        SelectRunnable(group.Rows, _isFeatureContext, IncludeAiWorkflows).Count,
        SelectRerunnable(group.Rows, _isFeatureContext, IncludeAiWorkflows).Count);

    private bool BulkActionsDisabled => isRerunningAll || isRefreshing || isLoading;

    /// <summary>Starts every workflow of the group that can be started. Levels are not chained and nothing waits for a run.</summary>
    internal Task RunLevelAsync(ActionRowGroup group) =>
        BulkActionsDisabled
            ? Task.CompletedTask
            : RunBulkAsync(
                SelectRunnable(group.Rows, _isFeatureContext, IncludeAiWorkflows),
                "Starting", "Run level", "Started", GroupTitle(group),
                entry => GitHubActionsService.RunWorkflowAsync(entry));

    /// <summary>Re-runs every failed workflow of the group.</summary>
    internal Task RerunLevelAsync(ActionRowGroup group) =>
        BulkActionsDisabled
            ? Task.CompletedTask
            : RunBulkAsync(
                SelectRerunnable(group.Rows, _isFeatureContext, IncludeAiWorkflows),
                "Re-running", "Re-run level", "Re-ran", GroupTitle(group),
                entry => GitHubActionsService.RerunWorkflowAsync(entry));
}
