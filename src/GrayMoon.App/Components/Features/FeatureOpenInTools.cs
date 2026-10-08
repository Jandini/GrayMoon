namespace GrayMoon.App.Components.Features;

/// <summary>
/// Open-in tools for the feature picker. A tool appears on the button row only after it has been
/// used, and it stays in that slot. A tool used for the first time is appended.
/// Terminal and Explorer are remembered the same way as the IDEs.
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

    /// <summary>
    /// Remembers a tool the first time it is used, after tools already on the row.
    /// A tool that is already remembered stays where it is.
    /// </summary>
    public static IReadOnlyList<string> RecordUse(IReadOnlyList<string> recent, string toolId)
    {
        if (!IsRemembered(toolId))
            return recent;

        foreach (var existing in recent)
        {
            if (string.Equals(existing, toolId, StringComparison.Ordinal))
                return recent;
        }

        var next = new List<string>(recent.Count + 1);
        foreach (var existing in recent)
        {
            if (IsRemembered(existing))
                next.Add(existing);
        }

        next.Add(toolId);
        return next;
    }

    /// <summary>
    /// Tools from <paramref name="recent"/> that can be launched. Being listed is not enough:
    /// Cursor, Claude CLI, VS Code, and Visual Studio are omitted unless installed.
    /// Terminal and Explorer stay, because Desktop can always open them.
    /// </summary>
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
