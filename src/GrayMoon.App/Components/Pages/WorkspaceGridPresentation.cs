using GrayMoon.Application.Workspaces;

namespace GrayMoon.App.Components.Pages;

/// <summary>
/// What the Repositories grid and its header show for one workspace profile. Built once per page load
/// from <see cref="WorkspaceCapabilities"/>, so the markup reads presentation flags and never asks the
/// capability record (or the workspace type) itself. Same page, same queries: a Basic workspace simply
/// renders fewer columns, no dependency metric, no level grouping and no dependency actions.
/// </summary>
public sealed record WorkspaceGridPresentation(
    bool ShowVersionColumn,
    bool ShowDependencyMetrics,
    bool GroupByDependencyLevel,
    bool ShowDependencyUpdateActions,
    bool ShowPackageRestore,
    bool ShowCiLinks)
{
    /// <summary>Repository, Branch and the combined metrics column are always present.</summary>
    private const int AlwaysShownColumnCount = 3;

    /// <summary>Number of table columns, used as the colspan of every full-width row (level headers, error rows, spacers, placeholders).</summary>
    public int ColumnCount => AlwaysShownColumnCount + (ShowVersionColumn ? 1 : 0);

    /// <summary>
    /// A workspace without dependency-aware update gets file-version updating as its own clearly named
    /// header action. A dependency workspace keeps reaching it through the Update menu, as before.
    /// </summary>
    public bool ShowFileVersionUpdateAction => !ShowDependencyUpdateActions;

    /// <summary>
    /// Bulk "Merge PRs..." lives in the level-header menu. A flat list has no level headers, so the header's
    /// Branch/Feature menu offers it for every repository instead.
    /// </summary>
    public bool ShowBulkMergeInHeaderMenu => !GroupByDependencyLevel;

    /// <summary>Modifier for the metrics grid (header icons and row badges) so it lays out four blocks instead of five when the dependency block is absent.</summary>
    public string MetricsGridModifierClass => ShowDependencyMetrics ? string.Empty : "metrics-grid--no-deps";

    /// <summary>The pre-profile presentation, used before capabilities are known and for any caller that does not state a profile.</summary>
    public static WorkspaceGridPresentation Legacy { get; } = For(WorkspaceCapabilities.Legacy);

    public static WorkspaceGridPresentation For(WorkspaceCapabilities? capabilities)
    {
        var c = capabilities ?? WorkspaceCapabilities.Legacy;
        return new WorkspaceGridPresentation(
            ShowVersionColumn: c.UsesRepositoryVersioning,
            ShowDependencyMetrics: c.UsesDependencyGraph,
            GroupByDependencyLevel: c.UsesDependencyGraph,
            ShowDependencyUpdateActions: c.UsesDependencyAwareUpdate,
            ShowPackageRestore: c.UsesPackageRestore,
            ShowCiLinks: c.UsesCiIntegration);
    }
}
