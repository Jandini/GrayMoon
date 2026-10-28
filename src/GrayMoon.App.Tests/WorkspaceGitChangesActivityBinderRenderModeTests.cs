using System.Reflection;
using GrayMoon.App.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceGitChangesActivityBinderRenderModeTests
{
    /// <summary>
    /// MainLayout is rendered statically, so a binder that is not an interactive island gains and releases its
    /// activity lease within one HTTP request and an open page never holds it (Git Changes then stops being
    /// monitored 8 minutes after the last navigation).
    /// </summary>
    [Fact]
    public void Binder_is_an_interactive_server_island_without_prerender()
    {
        var attribute = typeof(WorkspaceGitChangesActivityBinder).GetCustomAttribute<RenderModeAttribute>();

        Assert.NotNull(attribute);
        var mode = Assert.IsType<InteractiveServerRenderMode>(attribute!.Mode);
        Assert.False(mode.Prerender);
    }
}
