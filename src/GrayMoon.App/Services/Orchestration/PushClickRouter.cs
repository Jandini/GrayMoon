using GrayMoon.Application.Workspaces;

namespace GrayMoon.App.Services.Orchestration;

/// <summary>What the Repositories page does when the header Push button is clicked.</summary>
public enum PushClickRoute
{
    /// <summary>Start the push job straight away, with no dependency dialog.</summary>
    StartDirect,

    /// <summary>Look up the push dependency info before deciding (dependency-aware workspaces only).</summary>
    ResolveDependencies,

    /// <summary>Open the "push with dependencies" dialog.</summary>
    ShowDependencyModal,

    /// <summary>The dependency info could not be loaded; tell the user to try again.</summary>
    LoadFailed,
}

/// <summary>
/// The pure routing rule behind the header Push button, extracted from the page so it can be tested without
/// rendering it. A workspace that is not dependency-aware (Basic) must start the push directly: it has no
/// dependency graph to resolve, so there is no dialog to show and nothing to wait for.
/// </summary>
public static class PushClickRouter
{
    /// <summary>First decision, made before any dependency lookup.</summary>
    public static PushClickRoute ForCapabilities(WorkspaceCapabilities? capabilities)
        => capabilities is null || !capabilities.UsesDependencyAwarePush
            ? PushClickRoute.StartDirect
            : PushClickRoute.ResolveDependencies;

    /// <summary>Second decision for dependency-aware workspaces, once the dependency lookup has run.</summary>
    /// <param name="dependencyInfoLoaded">False when the lookup returned nothing.</param>
    /// <param name="hasNoDependencies">The pushed set requires no packages and depends on no repositories.</param>
    /// <param name="anyDependencyNeedsPush">At least one dependency repository itself has commits to push.</param>
    public static PushClickRoute ForDependencies(bool dependencyInfoLoaded, bool hasNoDependencies, bool anyDependencyNeedsPush)
    {
        if (!dependencyInfoLoaded)
            return PushClickRoute.LoadFailed;
        if (hasNoDependencies || !anyDependencyNeedsPush)
            return PushClickRoute.StartDirect;
        return PushClickRoute.ShowDependencyModal;
    }
}
