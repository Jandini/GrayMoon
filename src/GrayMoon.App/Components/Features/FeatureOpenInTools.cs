namespace GrayMoon.App.Components.Features;

/// <summary>
/// One Open-in tool. <see cref="Id"/> is stored in the workspace database and is also the key in
/// GrayMoon.Desktop's tool-availability message. <see cref="DesktopCommand"/> is the WebView2
/// message GrayMoon.Desktop handles to launch it.
/// </summary>
internal sealed record OpenInTool(
    string Id,
    string Label,
    string DesktopCommand,
    bool RequiresInstall,
    string? IconImageSrc = null,
    string? IconClass = null)
{
    public string Title => "Open in " + Label;
}

/// <summary>
/// Open-in tools for the feature picker. A tool appears on the button row only when the user
/// adds it, and it stays in that slot until they remove it. Using a tool does not add it.
/// Terminal and Explorer are pinned the same way as the IDEs.
/// </summary>
/// <remarks>
/// To add a tool: add it to <see cref="All"/>, add its install check to
/// <c>InstalledOpenInTools</c>, add the icon under <c>wwwroot/icons</c>, and teach
/// GrayMoon.Desktop the same id and command (its <c>OpenInToolCatalog</c> and
/// <c>ToolAvailabilityService</c>).
/// </remarks>
internal static class FeatureOpenInTools
{
    public const string Cursor = "cursor";
    public const string ClaudeCli = "claudeCli";
    public const string CodexCli = "codexCli";
    public const string VsCode = "vsCode";
    public const string VisualStudio = "visualStudio";
    public const string Terminal = "terminal";
    public const string Explorer = "explorer";

    /// <summary>
    /// Every tool, in Open in... menu order. Tools that need an install come first; the menu
    /// shows only the installed ones. Terminal and Explorer are always there on Desktop.
    /// </summary>
    public static readonly IReadOnlyList<OpenInTool> All =
    [
        new(Cursor, "Cursor", "OpenInCursor", RequiresInstall: true, IconImageSrc: "icons/cursor.svg"),
        new(ClaudeCli, "Claude CLI", "OpenInClaudeCli", RequiresInstall: true, IconImageSrc: "icons/claude.svg"),
        new(CodexCli, "Codex CLI", "OpenInCodexCli", RequiresInstall: true, IconImageSrc: "icons/codex.svg"),
        new(VsCode, "VS Code", "OpenInVsCode", RequiresInstall: true, IconImageSrc: "icons/visualstudiocode.svg"),
        new(VisualStudio, "Visual Studio", "OpenInVisualStudio", RequiresInstall: true, IconImageSrc: "icons/visualstudio.svg"),
        new(Terminal, "Terminal", "OpenInTerminal", RequiresInstall: false, IconClass: "bi-terminal"),
        new(Explorer, "Explorer", "ShowInExplorer", RequiresInstall: false, IconClass: "bi-folder2-open"),
    ];

    private static readonly Dictionary<string, OpenInTool> ById =
        All.ToDictionary(t => t.Id, StringComparer.Ordinal);

    public static OpenInTool? Find(string? toolId) =>
        toolId is not null && ById.TryGetValue(toolId, out var tool) ? tool : null;

    public static bool IsRemembered(string? toolId) => Find(toolId) is not null;

    /// <summary>
    /// Whether the tool can be launched: it needs no install, or it is in <paramref name="installed"/>.
    /// </summary>
    public static bool IsAvailable(OpenInTool tool, IReadOnlySet<string> installed) =>
        !tool.RequiresInstall || installed.Contains(tool.Id);

    /// <summary>
    /// Toast shown when an Open-in tool is launched, so the click is visible while it starts.
    /// </summary>
    public static string StartToastMessage(OpenInTool tool) => $"Starting {tool.Label}...";

    /// <summary>
    /// Adds a tool after the ones already on the row. A tool that is already there stays where it is.
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
    /// Drops one remembered tool. The others stay in order. An unknown id leaves the list unchanged.
    /// </summary>
    public static IReadOnlyList<string> Remove(IReadOnlyList<string> recent, string toolId)
    {
        if (!IsRemembered(toolId))
            return recent;

        var next = new List<string>(recent.Count);
        var removed = false;
        foreach (var existing in recent)
        {
            if (!removed && string.Equals(existing, toolId, StringComparison.Ordinal))
            {
                removed = true;
                continue;
            }

            if (IsRemembered(existing))
                next.Add(existing);
        }

        return removed ? next : recent;
    }

    /// <summary>
    /// Tools from <paramref name="recent"/> that can be launched. Being listed is not enough:
    /// a tool that needs an install is omitted unless it is in <paramref name="installed"/>.
    /// Terminal and Explorer stay, because Desktop can always open them.
    /// </summary>
    public static IReadOnlyList<OpenInTool> VisibleButtons(IReadOnlyList<string> recent, IReadOnlySet<string> installed)
    {
        var buttons = new List<OpenInTool>();
        foreach (var toolId in recent)
        {
            if (Find(toolId) is { } tool && IsAvailable(tool, installed) && !buttons.Contains(tool))
                buttons.Add(tool);
        }

        return buttons;
    }
}
