namespace GrayMoon.App.Services.GitHub;

/// <summary>
/// Shared "is this an AI-driven workflow" predicate used both to decide what to display
/// (WorkspaceActions) and what to fetch from GitHub in the first place (GitHubActionsService),
/// so hiding AI workflows also skips the per-workflow GitHub API calls made for them.
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
