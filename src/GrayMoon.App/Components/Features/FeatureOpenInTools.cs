namespace GrayMoon.App.Components.Features;

/// <summary>
/// Open-in tools for the feature picker. A tool appears on the button row only after it has been
/// used, most recent first. Terminal and Explorer are remembered the same way as the IDEs.
/// </summary>
internal static class FeatureOpenInTools
{
    public const string Cursor = "cursor";
    public const string ClaudeCli = "claudeCli";
    public const string VsCode = "vsCode";
    public const string VisualStudio = "visualStudio";
    public const string Terminal = "terminal";
    public const string Explorer = "explorer";

    private static readonly HashSet<string> RememberedTools = new(StringComparer.Ordinal)
    {
        Cursor,
        ClaudeCli,
        VsCode,
        VisualStudio,
        Terminal,
        Explorer,
    };

    public static bool IsRemembered(string? toolId) =>
        toolId is not null && RememberedTools.Contains(toolId);

    public static string? FromLaunchMethod(string method) => method switch
    {
        "openInCursor" => Cursor,
        "openInClaudeCli" => ClaudeCli,
        "openInVsCode" => VsCode,
        "openInVisualStudio" => VisualStudio,
        "openInTerminal" => Terminal,
        "showInExplorer" => Explorer,
        _ => null,
    };

    public static string LaunchMethod(string toolId) => toolId switch
    {
        Cursor => "openInCursor",
        ClaudeCli => "openInClaudeCli",
        VsCode => "openInVsCode",
        VisualStudio => "openInVisualStudio",
        Terminal => "openInTerminal",
        Explorer => "showInExplorer",
        _ => throw new ArgumentOutOfRangeException(nameof(toolId), toolId, "Unknown open-in tool."),
    };

    public static string Label(string toolId) => toolId switch
    {
        Cursor => "Cursor",
        ClaudeCli => "Claude CLI",
        VsCode => "VS Code",
        VisualStudio => "Visual Studio",
        Terminal => "Terminal",
        Explorer => "Explorer",
        _ => toolId,
    };

    public static string Title(string toolId) => "Open in " + Label(toolId);

    public static string? IconImageSrc(string toolId) => toolId switch
    {
        Cursor => "icons/cursor.svg",
        ClaudeCli => "icons/claude.svg",
        VsCode => "icons/visualstudiocode.svg",
        VisualStudio => "icons/visualstudio.svg",
        _ => null,
    };

    public static string IconClass(string toolId) => toolId switch
    {
        Terminal => "bi-terminal",
        Explorer => "bi-folder2-open",
        _ => "bi-box",
    };

    /// <summary>Moves a remembered tool to the front.</summary>
    public static IReadOnlyList<string> RecordUse(IReadOnlyList<string> recent, string toolId)
    {
        if (!IsRemembered(toolId))
            return recent;

        var next = new List<string> { toolId };
        foreach (var existing in recent)
        {
            if (IsRemembered(existing) && !string.Equals(existing, toolId, StringComparison.Ordinal))
                next.Add(existing);
        }

        return next;
    }

    /// <summary>Recent tools that can be launched. IDEs are omitted when they are not installed.</summary>
    public static IReadOnlyList<string> VisibleButtons(
        IReadOnlyList<string> recent,
        bool cursor,
        bool claudeCli,
        bool vsCode,
        bool visualStudio)
    {
        var buttons = new List<string>();
        foreach (var toolId in recent)
        {
            if (IsAvailable(toolId, cursor, claudeCli, vsCode, visualStudio) && !buttons.Contains(toolId))
                buttons.Add(toolId);
        }

        return buttons;
    }

    private static bool IsAvailable(string toolId, bool cursor, bool claudeCli, bool vsCode, bool visualStudio) =>
        toolId switch
        {
            Cursor => cursor,
            ClaudeCli => claudeCli,
            VsCode => vsCode,
            VisualStudio => visualStudio,
            Terminal or Explorer => true,
            _ => false,
        };
}
