using GrayMoon.App.Services.Security;

namespace GrayMoon.App.Tests;

/// <summary>
/// The sidebar toggle saves through POST /api/settings/sidebar-collapsed. A browser sends Origin on that
/// POST, and <see cref="RequestSecurityMiddleware"/> rejects it unless it also carries X-GrayMoon-Request: 1.
/// Without the header the class flips on screen and the next page renders the stored (still collapsed) value.
/// </summary>
public class SidebarCollapseScriptTests
{
    [Fact]
    public void Sidebar_toggle_sends_the_header_a_browser_post_needs()
    {
        var toggle = SidebarToggleScript();
        const string marker = "'X-GrayMoon-Request': '";
        var at = toggle.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(
            at >= 0,
            "graymoonToggleSidebar must send X-GrayMoon-Request or the save is rejected and the next page collapses the sidebar.");

        var valueStart = at + marker.Length;
        var valueEnd = toggle.IndexOf('\'', valueStart);
        Assert.True(valueEnd > valueStart);
        var headerValue = toggle[valueStart..valueEnd];

        Assert.True(RequestSecurityMiddleware.IsRestRequestAllowed(
            origin: "http://localhost:8384",
            requestHeaderValue: headerValue,
            allowedHosts: []));
    }

    private static string SidebarToggleScript()
    {
        var source = File.ReadAllText(FindAppRazor());
        const string startMarker = "window.graymoonToggleSidebar";
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, "App.razor no longer defines graymoonToggleSidebar.");

        var end = source.IndexOf("Collapsed sidebar nav hints", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source[start..end];
    }

    private static string FindAppRazor()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "GrayMoon.App", "Components", "App.razor");
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(dir.FullName, "src", "GrayMoon.App", "Components", "App.razor");
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find GrayMoon.App/Components/App.razor from the test output directory.");
    }
}
