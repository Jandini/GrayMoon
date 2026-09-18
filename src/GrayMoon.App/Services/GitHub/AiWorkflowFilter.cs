namespace GrayMoon.App.Services.GitHub;

/// <summary>
/// Shared "is this an AI-driven workflow" predicate used by WorkspaceActions to count the AI badge
/// and to hide those workflows from the grid and status chips while the badge is off.
/// Case-insensitive match on the GitHub-reported workflow name/path only - never reads workflow YAML file contents.
/// </summary>
public static class AiWorkflowFilter
{
    private static readonly string[] Keywords = ["copilot", "dependabot"];

    public static bool IsAiWorkflow(string? name, string? path) =>
        Keywords.Any(keyword =>
            (name ?? string.Empty).Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            (path ?? string.Empty).Contains(keyword, StringComparison.OrdinalIgnoreCase));
}
