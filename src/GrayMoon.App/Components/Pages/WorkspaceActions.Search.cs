using GrayMoon.Common.Search;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceActions
{
    private void ApplyIncomingSearchQuery()
    {
        var incoming = SearchQuery ?? string.Empty;
        if (string.Equals(incoming, _appliedSearchQuery, StringComparison.Ordinal))
            return;

        _appliedSearchQuery = incoming;
        searchTerm = incoming;
    }

    internal void OnSearchChanged(ChangeEventArgs e)
    {
        searchTerm = e.Value?.ToString() ?? string.Empty;
        StateHasChanged();
    }

    internal void ClearSearchFilter()
    {
        searchTerm = string.Empty;
        StateHasChanged();
    }

    internal void OnSearchKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            searchTerm = string.Empty;
            StateHasChanged();
        }
    }

    internal void ToggleShowErrors()
    {
        _showErrors = !_showErrors;
        StateHasChanged();
    }

    internal void ToggleShowFailed()
    {
        _showFailed = !_showFailed;
        StateHasChanged();
    }

    internal void ToggleShowAborted()
    {
        _showAborted = !_showAborted;
        StateHasChanged();
    }

    internal void ToggleShowRunning()
    {
        _showRunning = !_showRunning;
        StateHasChanged();
    }

    internal void ToggleShowSuccess()
    {
        _showSuccess = !_showSuccess;
        StateHasChanged();
    }

    internal void ToggleShowNone()
    {
        _showNone = !_showNone;
        StateHasChanged();
    }

    internal async Task ToggleExcludeAiWorkflowsAsync()
    {
        if (workspace == null || _isTogglingAiFilter)
            return;

        var exclude = !workspace.ExcludeAiWorkflows;

        try
        {
            _isTogglingAiFilter = true;
            StateHasChanged();

            await WorkspaceRepository.UpdateExcludeAiWorkflowsAsync(WorkspaceId, exclude);
            workspace.ExcludeAiWorkflows = exclude;
            await LoadWorkspaceAsync();
        }
        finally
        {
            _isTogglingAiFilter = false;
            StateHasChanged();
        }
    }

    internal static IEnumerable<WorkflowActionLine> LinesForDisplay(WorkspaceActionRow row)
    {
        if (row.WorkflowLines.Count > 0)
            return row.WorkflowLines;
        return [new WorkflowActionLine()];
    }

    /// <summary>Workflow lines to render after applying status filter toggles (repo errors: all lines or none).</summary>
    internal IEnumerable<WorkflowActionLine> LinesForGrid(WorkspaceActionRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.ErrorMessage))
        {
            if (!_showErrors)
                yield break;
            foreach (var line in LinesForDisplay(row))
                yield return line;
            yield break;
        }

        foreach (var line in LinesForDisplay(row))
        {
            if (IsBucketVisible(GetLineFilterBucket(row, line)))
                yield return line;
        }
    }

    /// <summary>Lines to render after status toggles and repository/workflow text search.</summary>
    internal IEnumerable<WorkflowActionLine> LinesForTable(WorkspaceActionRow row)
    {
        foreach (var line in LinesForGrid(row))
        {
            if (LineMatchesSearch(row, line))
                yield return line;
        }
    }

    private bool LineMatchesSearch(WorkspaceActionRow row, WorkflowActionLine line) =>
        MatchesSearch(row, line, searchTerm);

    internal static bool MatchesSearch(WorkspaceActionRow row, WorkflowActionLine line, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return FilterSearchMatcher.Matches(query, term => MatchesActionLineTerm(row, line, term));
    }

    private static bool MatchesActionLineTerm(WorkspaceActionRow row, WorkflowActionLine line, FilterSearchTerm term)
    {
        if (!string.IsNullOrEmpty(term.Field))
        {
            return term.Field switch
            {
                "repo" => (row.Repo.RepositoryName ?? string.Empty)
                    .Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                "workflow" => (line.Action?.WorkflowName ?? string.Empty)
                    .Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        var repo = row.Repo.RepositoryName ?? string.Empty;
        var workflow = line.Action?.WorkflowName ?? string.Empty;
        var haystack = $"{repo} {workflow}";
        return haystack.Contains(term.Value, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsBucketVisible(ActionLineFilterBucket bucket) =>
        bucket switch
        {
            ActionLineFilterBucket.Failed => _showFailed,
            ActionLineFilterBucket.Running => _showRunning,
            ActionLineFilterBucket.Aborted => _showAborted,
            ActionLineFilterBucket.Success => _showSuccess,
            ActionLineFilterBucket.None => _showNone,
            _ => true
        };

    /// <summary>Matches <see cref="ActionBadge"/> status order: failed, running, aborted, success, else none.</summary>
    private static ActionLineFilterBucket GetLineFilterBucket(WorkspaceActionRow row, WorkflowActionLine line)
    {
        if (IsLineFailedForBranch(row, line))
            return ActionLineFilterBucket.Failed;
        if (IsLineRunningForBranch(row, line))
            return ActionLineFilterBucket.Running;
        if (IsLineAbortedForBranch(row, line))
            return ActionLineFilterBucket.Aborted;
        if (IsLineSuccessForBranch(row, line))
            return ActionLineFilterBucket.Success;
        return ActionLineFilterBucket.None;
    }
}
