namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceActions
{
    internal void OpenLogsModal(WorkspaceActionRow row, WorkflowActionLine line)
    {
        _logsConnectorName = row.Repo.ConnectorName;
        _logsOwner = row.Repo.OrgName ?? "";
        _logsRepo = row.Repo.RepositoryName;
        _logsRunId = line.Action!.RunId!.Value;
        _logsWorkflowName = line.Action.WorkflowName;
        _logsModalVisible = true;
        StateHasChanged();
    }

    internal void CloseLogsModal()
    {
        _logsModalVisible = false;
        StateHasChanged();
    }

    internal void OnLogsRerunTriggered()
    {
        var runId = _logsRunId;
        var row = rows.FirstOrDefault(r => r.WorkflowLines.Any(l => l.Action?.RunId == runId));
        var line = row?.WorkflowLines.FirstOrDefault(l => l.Action?.RunId == runId);
        var workflowId = line?.Action?.WorkflowId ?? 0;

        CloseLogsModal();

        if (row == null || workflowId <= 0) return;

        var latch = GetOrCreateLatch(row, workflowId);
        latch.RerunPending = true;
        ApplyActionLatches(row);
        StateHasChanged();
        _ = TryRefreshAfterJobRerunAsync(row, workflowId);
    }

    private async Task TryRefreshAfterJobRerunAsync(WorkspaceActionRow row, long workflowId)
    {
        try
        {
            await TryRefreshUntilWorkflowLineRunningAsync(row, workflowId, "GHA Re-run (job)", CancellationToken.None);
        }
        finally
        {
            EndRerunLatch(row, workflowId);
            ApplyActionLatches(row);
            await InvokeAsync(StateHasChanged);
        }
    }
}
