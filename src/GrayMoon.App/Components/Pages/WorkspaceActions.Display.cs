namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceActions
{
    private static string GetFriendlyErrorMessage(Exception ex) =>
        ex switch
        {
            HttpRequestException httpEx => GitHubApiErrorHelper.FormatFriendlyGitHubHttpError(httpEx),
            OperationCanceledException =>
                "The operation was cancelled (for example, refresh was interrupted). Try Run again or use Refresh.",
            _ => ex.Message
        };

    internal static int GetStatusSortOrder(WorkspaceActionRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.ErrorMessage)) return 0;
        var status = GetEffectiveStatusForSort(row);
        return status switch
        {
            "failed" => 1,
            "aborted" => 2,
            "running" => 3,
            "success" => 4,
            _ => 5
        };
    }

    private static string? GetEffectiveStatusForSort(WorkspaceActionRow row)
    {
        var order = 5;
        string? worst = null;
        foreach (var line in row.WorkflowLines)
        {
            var a = line.Action;
            if (a == null || !string.Equals(a.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase))
                continue;
            var o = a.Status switch
            {
                "failed" => 1,
                "aborted" => 2,
                "running" => 3,
                "success" => 4,
                _ => 5
            };
            if (o < order)
            {
                order = o;
                worst = a.Status;
            }
        }

        return worst;
    }

    private static bool IsLineFailedForBranch(WorkspaceActionRow row, WorkflowActionLine line) =>
        line.Action != null &&
        string.Equals(line.Action.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(line.Action.Status, "failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsLineRunningForBranch(WorkspaceActionRow row, WorkflowActionLine line) =>
        line.Action != null &&
        string.Equals(line.Action.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(line.Action.Status, "running", StringComparison.OrdinalIgnoreCase);

    private static bool IsLineSuccessForBranch(WorkspaceActionRow row, WorkflowActionLine line) =>
        line.Action != null &&
        string.Equals(line.Action.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(line.Action.Status, "success", StringComparison.OrdinalIgnoreCase);

    private static bool IsLineAbortedForBranch(WorkspaceActionRow row, WorkflowActionLine line) =>
        line.Action != null &&
        string.Equals(line.Action.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(line.Action.Status, "aborted", StringComparison.OrdinalIgnoreCase);

    private static bool IsLineNoneForBranch(WorkspaceActionRow row, WorkflowActionLine line) =>
        line.Action == null ||
        !string.Equals(line.Action.BranchName, row.Link.BranchName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(line.Action.Status, "none", StringComparison.OrdinalIgnoreCase);

    internal static bool CanRerun(WorkspaceActionRow row, WorkflowActionLine line) =>
        IsLineFailedForBranch(row, line) && (line.Action?.RunId ?? 0) > 0;

    internal static bool CanRun(WorkspaceActionRow row, WorkflowActionLine line) =>
        (line.Action?.SupportsWorkflowDispatch ?? false) &&
        (line.Action?.WorkflowId ?? 0) > 0 &&
        !string.IsNullOrWhiteSpace(row.Link.BranchName);

    /// <summary>Failed run that also supports workflow_dispatch: show Re-run as a split button with a "Run again" (new dispatch) option.</summary>
    internal static bool CanRunAgain(WorkspaceActionRow row, WorkflowActionLine line) =>
        CanRerun(row, line) && CanRun(row, line);

    internal static bool CanAbort(WorkspaceActionRow row, WorkflowActionLine line) =>
        IsLineRunningForBranch(row, line) && (line.Action?.RunId ?? 0) > 0;

    /// <summary>True when Run should be non-interactive: workflow_dispatch request in flight.</summary>
    internal static bool IsRunWorkflowBusy(WorkspaceActionRow row, WorkflowActionLine line) =>
        line.RunInProgress;

    /// <summary>GitHub Actions workflow page (not a specific run); uses persisted <see cref="ActionStatusInfo.WorkflowHtmlUrl"/> or builds from repo + workflow id.</summary>
    internal static string? GetWorkflowPageUrl(WorkspaceActionRow row, WorkflowActionLine line)
    {
        if (line.Action == null)
            return null;
        return RepositoryUrlHelper.BuildWorkflowPageUrl(
            line.Action.WorkflowHtmlUrl,
            row.Repo.CloneUrl,
            row.Repo.OrgName,
            row.Repo.RepositoryName,
            line.Action.WorkflowId ?? 0,
            line.Action.WorkflowPath,
            null);
    }

    internal static string GroupStripeClass(int groupIndex) =>
        (groupIndex % 2 == 0) ? "actions-group-even" : "actions-group-odd";

    internal static string FormatLastRun(DateTimeOffset? updatedAt)
    {
        if (updatedAt == null) return string.Empty;
        var elapsed = DateTimeOffset.UtcNow - updatedAt.Value;
        if (elapsed.TotalSeconds < 60) return "just now";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} min ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours} hr ago";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays} days ago";
        return updatedAt.Value.LocalDateTime.ToString("MMM d, h:mm tt");
    }
}
