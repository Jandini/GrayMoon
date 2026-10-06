using GrayMoon.Application.Workspaces;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>The workspace-scoped pages under <c>/workspaces/{id}/...</c>, in left-navigation order.</summary>
public enum WorkspacePage
{
    Repositories,
    Changes,
    Projects,
    Packages,
    Files,
    Dependencies,
    Actions
}

/// <summary>Outcome of checking whether a workspace page may load. Only <see cref="Available"/> runs the page's queries.</summary>
public enum WorkspacePageAccessOutcome
{
    Available,
    NotAvailable,
    WorkspaceNotFound
}

/// <summary>
/// The single page/navigation policy (design section 11). The left navigation and every gated page ask this,
/// so no page invents its own workspace-type check and direct navigation follows the same rules as the nav.
/// </summary>
public static class WorkspacePageAccess
{
    public static IReadOnlyList<WorkspacePage> NavigationOrder { get; } =
    [
        WorkspacePage.Repositories,
        WorkspacePage.Changes,
        WorkspacePage.Projects,
        WorkspacePage.Packages,
        WorkspacePage.Files,
        WorkspacePage.Dependencies,
        WorkspacePage.Actions
    ];

    /// <summary>Actions follows the CI provider, never the workspace type.</summary>
    public static bool IsAvailable(WorkspacePage page, WorkspaceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return page switch
        {
            WorkspacePage.Repositories or WorkspacePage.Changes or WorkspacePage.Files => true,
            WorkspacePage.Projects => capabilities.DiscoversDotNetProjects,
            WorkspacePage.Packages => capabilities.UsesNuGetPackages,
            WorkspacePage.Dependencies => capabilities.UsesDependencyGraph,
            WorkspacePage.Actions => capabilities.UsesCiIntegration,
            _ => false
        };
    }

    /// <summary>Pages that need no capability. A missing workspace (null capabilities) shows only these; each handles "not found" itself.</summary>
    public static bool IsAlwaysAvailable(WorkspacePage page) =>
        page is WorkspacePage.Repositories or WorkspacePage.Changes or WorkspacePage.Files;

    /// <summary>The navigation item set, in order. Null capabilities (missing workspace) yields only the always-available pages.</summary>
    public static IReadOnlyList<WorkspacePage> GetAvailablePages(WorkspaceCapabilities? capabilities) =>
        NavigationOrder
            .Where(page => capabilities is null ? IsAlwaysAvailable(page) : IsAvailable(page, capabilities))
            .ToList();

    public static WorkspacePageAccessOutcome Evaluate(WorkspacePage page, WorkspaceCapabilities? capabilities)
    {
        if (capabilities is null)
            return WorkspacePageAccessOutcome.WorkspaceNotFound;

        return IsAvailable(page, capabilities)
            ? WorkspacePageAccessOutcome.Available
            : WorkspacePageAccessOutcome.NotAvailable;
    }

    /// <summary>The standard message for a page that cannot load. Empty for <see cref="WorkspacePageAccessOutcome.Available"/>.</summary>
    public static string GetUnavailableMessage(WorkspacePage page, WorkspacePageAccessOutcome outcome) => outcome switch
    {
        WorkspacePageAccessOutcome.Available => string.Empty,
        WorkspacePageAccessOutcome.WorkspaceNotFound => "Workspace not found.",
        _ => page switch
        {
            WorkspacePage.Actions => "CI is not enabled for this workspace.",
            WorkspacePage.Projects or WorkspacePage.Packages or WorkspacePage.Dependencies =>
                $"{page} are only available for .NET Dependency workspaces.",
            _ => $"{page} is not available for this workspace."
        }
    };
}
