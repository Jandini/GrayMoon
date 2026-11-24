using GrayMoon.App.Components.Pages;
using GrayMoon.App.Models;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceActionsLevelActionsTests
{
    [Fact]
    public void Run_and_Re_run_take_only_the_rows_of_the_group()
    {
        var inLevel = CreateRow("in-level", level: 2, Line("failed", workflowId: 1, runId: 10));
        var otherLevel = CreateRow("other-level", level: 1, Line("failed", workflowId: 2, runId: 20));
        var group = WorkspaceActions.BuildGroups([inLevel, otherLevel], groupByDependencyLevel: true).Single(g => g.LevelKey == 2);

        Assert.Equal(["in-level"], Names(WorkspaceActions.SelectRunnable(group.Rows, isFeatureContext: false, includeAiWorkflows: true)));
        Assert.Equal(["in-level"], Names(WorkspaceActions.SelectRerunnable(group.Rows, isFeatureContext: false, includeAiWorkflows: true)));
    }

    [Fact]
    public void Run_starts_every_dispatchable_workflow_that_is_not_running()
    {
        var beingStarted = Line("none", workflowId: 5, runId: null);
        beingStarted.RunInProgress = true;
        var row = CreateRow("widgets", level: 1,
            Line("success", workflowId: 1, runId: 10),
            Line("none", workflowId: 2, runId: null),
            Line("running", workflowId: 3, runId: 30),
            Line("success", workflowId: 4, runId: 40, supportsWorkflowDispatch: false),
            beingStarted);

        var selected = WorkspaceActions.SelectRunnable([row], isFeatureContext: false, includeAiWorkflows: true);

        Assert.Equal([1L, 2L], selected.Select(pair => pair.Line.Action!.WorkflowId!.Value));
    }

    [Fact]
    public void Re_run_takes_only_failed_workflows_that_have_a_run()
    {
        var row = CreateRow("widgets", level: 1,
            Line("failed", workflowId: 1, runId: 10),
            Line("failed", workflowId: 2, runId: null),
            Line("success", workflowId: 3, runId: 30),
            Line("failed", workflowId: 4, runId: 40, branchName: "another-branch"));

        var selected = WorkspaceActions.SelectRerunnable([row], isFeatureContext: false, includeAiWorkflows: true);

        Assert.Equal([1L], selected.Select(pair => pair.Line.Action!.WorkflowId!.Value));
    }

    [Fact]
    public void AI_workflows_are_left_out_while_the_AI_toggle_is_off()
    {
        var row = CreateRow("widgets", level: 1,
            Line("failed", workflowId: 1, runId: 10, workflowName: "Copilot Code Review"),
            Line("failed", workflowId: 2, runId: 20, workflowName: "CI"));

        Assert.Equal(["CI"], WorkflowNames(WorkspaceActions.SelectRunnable([row], isFeatureContext: false, includeAiWorkflows: false)));
        Assert.Equal(["CI"], WorkflowNames(WorkspaceActions.SelectRerunnable([row], isFeatureContext: false, includeAiWorkflows: false)));
        Assert.Equal(2, WorkspaceActions.SelectRunnable([row], isFeatureContext: false, includeAiWorkflows: true).Count);
    }

    [Fact]
    public void A_read_only_repository_in_the_group_is_skipped()
    {
        var readOnly = CreateRow("read-only", level: 1, Line("failed", workflowId: 1, runId: 10));
        readOnly.Link.IsReference = true;
        var writable = CreateRow("writable", level: 1, Line("failed", workflowId: 2, runId: 20));

        Assert.Equal(["writable"], Names(WorkspaceActions.SelectRunnable([readOnly, writable], isFeatureContext: false, includeAiWorkflows: true)));
        Assert.Equal(["writable"], Names(WorkspaceActions.SelectRerunnable([readOnly, writable], isFeatureContext: false, includeAiWorkflows: true)));
    }

    [Fact]
    public async Task A_workflow_that_cannot_be_started_gets_a_row_error_and_the_others_still_run()
    {
        var failing = CreateRow("failing", level: 1, Line("failed", workflowId: 1, runId: 10));
        var first = CreateRow("first", level: 1, Line("failed", workflowId: 2, runId: 20));
        var last = CreateRow("last", level: 1, Line("failed", workflowId: 3, runId: 30));
        var pairs = WorkspaceActions.SelectRerunnable([first, failing, last], isFeatureContext: false, includeAiWorkflows: true);
        var invoked = new List<string>();
        var completed = 0;

        var failed = await WorkspaceActions.InvokeBulkAsync(
            pairs,
            (row, _) =>
            {
                lock (invoked)
                    invoked.Add(row.Repo.RepositoryName);
                return row == failing ? Task.FromException(new InvalidOperationException("GitHub said no.")) : Task.CompletedTask;
            },
            maxConcurrency: 1,
            onItemCompleted: () =>
            {
                Interlocked.Increment(ref completed);
                return Task.CompletedTask;
            });

        Assert.Equal(["first", "failing", "last"], invoked);
        Assert.Equal(3, completed);
        Assert.Equal(["failing"], Names(failed));
        Assert.Equal("GitHub said no.", failing.ErrorMessage);
        Assert.Null(first.ErrorMessage);
        Assert.Null(last.ErrorMessage);
    }

    [Fact]
    public void The_closing_message_counts_what_started_and_names_what_failed()
    {
        Assert.Equal("Started 8 workflows in Level 2.", WorkspaceActions.BulkOutcomeMessage("Started", 8, [], "Level 2"));
        Assert.Equal("Re-ran 1 workflow.", WorkspaceActions.BulkOutcomeMessage("Re-ran", 1, [], scope: null));
        Assert.Equal(
            "Started 6 of 8 workflows in Level 2. Failed: Api.",
            WorkspaceActions.BulkOutcomeMessage("Started", 8, ["Api", "Api"], "Level 2"));
        Assert.Equal(
            "Started 0 of 4 workflows in Workspace. Failed: A, B, C and 1 more.",
            WorkspaceActions.BulkOutcomeMessage("Started", 4, ["A", "B", "C", "D"], "Workspace"));
    }

    [Fact]
    public void The_closing_message_says_how_many_runs_GitHub_does_not_show_yet()
    {
        Assert.Equal(
            "Started 8 workflows in Level 2. GitHub does not show 3 of them yet.",
            WorkspaceActions.BulkOutcomeMessage("Started", 8, [], "Level 2", notListedCount: 3));
    }

    [Fact]
    public void A_started_run_is_not_picked_up_while_the_row_still_shows_the_old_run()
    {
        var updatedAt = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
        var line = Line("failed", workflowId: 1, runId: 10);
        line.Action!.UpdatedAt = updatedAt;
        var row = CreateRow("widgets", level: 1, line);
        var started = WorkspaceActions.StartedRun.From(row, line);

        Assert.False(WorkspaceActions.IsRunPickedUp(started));

        // A refresh that returns the same run replaces the line object but changes nothing.
        var same = Line("failed", workflowId: 1, runId: 10);
        same.Action!.UpdatedAt = updatedAt;
        row.WorkflowLines = [same];
        Assert.False(WorkspaceActions.IsRunPickedUp(started));
    }

    [Fact]
    public void A_started_run_is_picked_up_when_it_runs_or_when_a_newer_run_is_listed()
    {
        var updatedAt = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
        var line = Line("failed", workflowId: 1, runId: 10);
        line.Action!.UpdatedAt = updatedAt;
        var row = CreateRow("widgets", level: 1, line);
        var started = WorkspaceActions.StartedRun.From(row, line);

        row.WorkflowLines = [Line("running", workflowId: 1, runId: 10)];
        Assert.True(WorkspaceActions.IsRunPickedUp(started));

        // A dispatched run that already finished: another run id.
        row.WorkflowLines = [Line("success", workflowId: 1, runId: 11)];
        Assert.True(WorkspaceActions.IsRunPickedUp(started));

        // A re-run that already finished: same run id, later update.
        var rerun = Line("success", workflowId: 1, runId: 10);
        rerun.Action!.UpdatedAt = updatedAt.AddMinutes(1);
        row.WorkflowLines = [rerun];
        Assert.True(WorkspaceActions.IsRunPickedUp(started));
    }

    [Fact]
    public void A_workflow_that_never_ran_is_picked_up_only_when_a_run_on_the_branch_is_listed()
    {
        var line = Line("none", workflowId: 1, runId: null);
        var row = CreateRow("widgets", level: 1, line);
        var started = WorkspaceActions.StartedRun.From(row, line);

        Assert.False(WorkspaceActions.IsRunPickedUp(started));

        row.WorkflowLines = [Line("success", workflowId: 1, runId: 5, branchName: "another-branch")];
        Assert.False(WorkspaceActions.IsRunPickedUp(started));

        row.WorkflowLines = [Line("running", workflowId: 1, runId: 6)];
        Assert.True(WorkspaceActions.IsRunPickedUp(started));
    }

    [Fact]
    public void Abort_takes_every_running_workflow_that_has_a_run_including_a_read_only_repository()
    {
        var beingAborted = Line("running", workflowId: 4, runId: 40);
        beingAborted.AbortInProgress = true;
        var row = CreateRow("widgets", level: 1,
            Line("running", workflowId: 1, runId: 10),
            Line("running", workflowId: 2, runId: null),
            Line("failed", workflowId: 3, runId: 30),
            beingAborted,
            Line("running", workflowId: 5, runId: 50, workflowName: "Copilot Code Review"));
        var readOnly = CreateRow("read-only", level: 1, Line("running", workflowId: 6, runId: 60));
        readOnly.Link.IsReference = true;

        var selected = WorkspaceActions.SelectAbortable([row, readOnly], includeAiWorkflows: false);

        Assert.Equal([1L, 6L], selected.Select(pair => pair.Line.Action!.WorkflowId!.Value));
        Assert.Empty(WorkspaceActions.SelectAbortable([CreateRow("idle", level: 1, Line("success", workflowId: 7, runId: 70))], includeAiWorkflows: true));
    }

    [Fact]
    public void An_aborted_run_is_stopped_once_the_row_no_longer_shows_it_running()
    {
        var line = Line("running", workflowId: 1, runId: 10);
        var row = CreateRow("widgets", level: 1, line);
        var aborted = WorkspaceActions.StartedRun.From(row, line);

        Assert.False(WorkspaceActions.IsRunStopped(aborted));

        row.WorkflowLines = [Line("aborted", workflowId: 1, runId: 10)];
        Assert.True(WorkspaceActions.IsRunStopped(aborted));
    }

    [Fact]
    public void A_group_header_shows_only_for_a_grouped_grid_with_a_visible_row()
    {
        var visible = CreateRow("visible", level: 1);
        var hidden = CreateRow("hidden", level: 2);
        var groups = WorkspaceActions.BuildGroups([visible, hidden], groupByDependencyLevel: true);
        var flat = Assert.Single(WorkspaceActions.BuildGroups([visible, hidden], groupByDependencyLevel: false));

        Assert.True(WorkspaceActions.ShowsGroupHeader(groups.Single(g => g.LevelKey == 1), row => row == visible));
        Assert.False(WorkspaceActions.ShowsGroupHeader(groups.Single(g => g.LevelKey == 2), row => row == visible));
        Assert.False(WorkspaceActions.ShowsGroupHeader(flat, _ => true));
    }

    [Fact]
    public void Group_titles_match_the_Repositories_grid()
    {
        var groups = WorkspaceActions.BuildGroups(
            [CreateRow("none", level: null), CreateRow("two", level: 2), CreateRow("workspace", level: 3, isWorkspaceRepository: true)],
            groupByDependencyLevel: true);

        Assert.Equal(["Workspace", "Level 2", "No dependencies"], groups.Select(WorkspaceActions.GroupTitle));
    }

    private static IEnumerable<string> Names(IEnumerable<(WorkspaceActions.WorkspaceActionRow Row, WorkspaceActions.WorkflowActionLine Line)> pairs) =>
        pairs.Select(pair => pair.Row.Repo.RepositoryName);

    private static IEnumerable<string?> WorkflowNames(IEnumerable<(WorkspaceActions.WorkspaceActionRow Row, WorkspaceActions.WorkflowActionLine Line)> pairs) =>
        pairs.Select(pair => pair.Line.Action!.WorkflowName);

    private static WorkspaceActions.WorkspaceActionRow CreateRow(
        string repositoryName,
        int? level,
        params WorkspaceActions.WorkflowActionLine[] lines) =>
        CreateRow(repositoryName, level, isWorkspaceRepository: false, lines);

    private static WorkspaceActions.WorkspaceActionRow CreateRow(
        string repositoryName,
        int? level,
        bool isWorkspaceRepository,
        params WorkspaceActions.WorkflowActionLine[] lines) => new()
    {
        Link = new WorkspaceRepositoryLink { BranchName = "main" },
        CiBranchName = "main",
        Repo = new GitHubRepositoryEntry { RepositoryName = repositoryName, OrgName = "acme", ConnectorName = "GitHub" },
        DependencyLevel = level,
        IsWorkspaceRepository = isWorkspaceRepository,
        WorkflowLines = [.. lines]
    };

    private static WorkspaceActions.WorkflowActionLine Line(
        string status,
        long workflowId,
        long? runId,
        bool supportsWorkflowDispatch = true,
        string branchName = "main",
        string workflowName = "CI") => new()
    {
        Action = new ActionStatusInfo
        {
            Status = status,
            BranchName = branchName,
            WorkflowId = workflowId,
            RunId = runId,
            WorkflowName = workflowName,
            SupportsWorkflowDispatch = supportsWorkflowDispatch
        }
    };
}
