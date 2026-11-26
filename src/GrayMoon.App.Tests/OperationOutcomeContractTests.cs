using System.Text.Json;
using GrayMoon.Application;

namespace GrayMoon.App.Tests;

/// <summary>
/// The typed outcome contract (per target, per step, with reason codes): what a caller that is not the UI reads
/// from it, how statuses combine, and what its JSON looks like.
/// </summary>
public sealed class OperationOutcomeContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string Worktree = "Worktree";
    private const string LocalBranch = "LocalBranch";
    private const string RemoteBranch = "RemoteBranch";

    [Fact]
    public void A_caller_that_is_not_the_UI_reads_a_partial_removal_from_JSON_without_parsing_text()
    {
        // 40 repositories: 37 removed, two sources blocked, the root waiting for them. The result is a failure with
        // an error text, and the outcome says exactly what happened to which repository.
        var result = OperationResult.Fail("Feature removal is incomplete.") with { Outcome = PartialRemoval() };

        // The transport: what a REST client or an MCP tool receives.
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, Web));
        var outcome = json.RootElement.GetProperty("outcome");

        Assert.Equal("Partial", outcome.GetProperty("status").GetString());
        Assert.True(outcome.GetProperty("needsAttention").GetBoolean());
        Assert.Equal("RemoveFeature", outcome.GetProperty("operation").GetString());
        Assert.Equal(7, outcome.GetProperty("workspaceId").GetInt32());
        Assert.Equal(12, outcome.GetProperty("contextId").GetInt32());
        Assert.Equal(1, outcome.GetProperty("contractVersion").GetInt32());
        Assert.Equal("Pending", outcome.GetProperty("derivedState").GetString());

        var byStatus = outcome.GetProperty("targets").EnumerateArray()
            .GroupBy(t => t.GetProperty("status").GetString())
            .ToDictionary(g => g.Key!, g => g.Count());
        Assert.Equal(37, byStatus["Succeeded"]);
        Assert.Equal(3, byStatus["Blocked"]);

        var blocked = outcome.GetProperty("targets").EnumerateArray()
            .Where(t => t.GetProperty("status").GetString() == "Blocked")
            .ToDictionary(
                t => t.GetProperty("target").GetProperty("name").GetString()!,
                t => t.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("step").GetString() == Worktree));
        Assert.Equal("UncommittedChanges", blocked["source-a"].GetProperty("reasonCode").GetString());
        Assert.Equal("DiscardUncommittedChanges", blocked["source-a"].GetProperty("requiredAuthorization").GetString());
        Assert.Equal("WorktreeLocked", blocked["source-b"].GetProperty("reasonCode").GetString());
        Assert.Equal("WaitingForOtherTargets", blocked["workspace-root"].GetProperty("reasonCode").GetString());
        Assert.True(blocked["workspace-root"].GetProperty("canRetry").GetBoolean());

        // A removed repository whose branch was kept is not a failure and not "everything done": it says both.
        var kept = outcome.GetProperty("targets").EnumerateArray()
            .First(t => t.GetProperty("target").GetProperty("name").GetString() == "source-c");
        Assert.Equal("Succeeded", kept.GetProperty("status").GetString());
        var keptBranch = kept.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("step").GetString() == LocalBranch);
        Assert.Equal("Skipped", keptBranch.GetProperty("status").GetString());
        Assert.Equal("UnmergedCommits", keptBranch.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public void The_typed_reader_answers_the_same_questions()
    {
        var outcome = PartialRemoval();

        Assert.Equal(OperationOutcomeStatus.Partial, outcome.Status);
        Assert.True(outcome.NeedsAttention);
        var counts = outcome.CountByStatus();
        Assert.Equal(37, counts[OperationOutcomeStatus.Succeeded]);
        Assert.Equal(3, counts[OperationOutcomeStatus.Blocked]);
        var root = outcome.ForWorkspaceRepository(100)!;
        Assert.Equal(OperationReasonCodes.WaitingForOtherTargets, root.Step(Worktree)!.ReasonCode);
        Assert.Null(root.Step("NoSuchStep"));
        Assert.Null(outcome.ForWorkspaceRepository(9999));
    }

    [Fact]
    public void A_result_without_an_outcome_has_no_outcome_property_and_one_with_it_round_trips()
    {
        Assert.DoesNotContain("outcome", JsonSerializer.Serialize(OperationResult.Ok(), Web), StringComparison.OrdinalIgnoreCase);

        var result = OperationResult.Ok() with { Outcome = PartialRemoval() };
        var back = JsonSerializer.Deserialize<OperationResult>(JsonSerializer.Serialize(result, Web), Web)!;

        Assert.NotNull(back.Outcome);
        Assert.Equal(result.Outcome!.OperationId, back.Outcome!.OperationId);
        Assert.Equal(OperationOutcomeStatus.Partial, back.Outcome.Status);
        Assert.Equal(40, back.Outcome.Targets.Count);
        var blocked = back.Outcome.ForWorkspaceRepository(38)!.Step(Worktree)!;
        Assert.Equal(OperationOutcomeStatus.Blocked, blocked.Status);
        Assert.Equal(OperationAuthorizations.DiscardUncommittedChanges, blocked.RequiredAuthorization);
        Assert.Equal(OperationFactGroups.WorktreeRegistration | OperationFactGroups.WorkingTree, blocked.Observed);
    }

    [Fact]
    public void Fact_groups_and_statuses_are_written_as_names_not_numbers()
    {
        var step = OperationStepOutcome.Unknown(Worktree, "No answer from the Worker.", OperationFactGroups.WorktreeRegistration | OperationFactGroups.LocalBranches);

        var json = JsonSerializer.Serialize(step, Web);

        Assert.Contains("\"status\":\"Unknown\"", json, StringComparison.Ordinal);
        Assert.Contains("\"reasonCode\":\"OutcomeUnknown\"", json, StringComparison.Ordinal);
        Assert.Contains("\"invalidated\":\"WorktreeRegistration, LocalBranches\"", json, StringComparison.Ordinal);
        Assert.Contains("\"observed\":\"None\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new OperationOutcomeStatus[0], OperationOutcomeStatus.NoOp)]
    [InlineData(new[] { OperationOutcomeStatus.Succeeded, OperationOutcomeStatus.Succeeded }, OperationOutcomeStatus.Succeeded)]
    [InlineData(new[] { OperationOutcomeStatus.Succeeded, OperationOutcomeStatus.NoOp, OperationOutcomeStatus.Skipped }, OperationOutcomeStatus.Succeeded)]
    [InlineData(new[] { OperationOutcomeStatus.NoOp, OperationOutcomeStatus.Skipped }, OperationOutcomeStatus.NoOp)]
    [InlineData(new[] { OperationOutcomeStatus.Skipped, OperationOutcomeStatus.Skipped }, OperationOutcomeStatus.Skipped)]
    [InlineData(new[] { OperationOutcomeStatus.Blocked, OperationOutcomeStatus.Skipped }, OperationOutcomeStatus.Blocked)]
    [InlineData(new[] { OperationOutcomeStatus.Failed, OperationOutcomeStatus.Failed }, OperationOutcomeStatus.Failed)]
    [InlineData(new[] { OperationOutcomeStatus.Cancelled, OperationOutcomeStatus.NoOp }, OperationOutcomeStatus.Cancelled)]
    [InlineData(new[] { OperationOutcomeStatus.Succeeded, OperationOutcomeStatus.Blocked }, OperationOutcomeStatus.Partial)]
    [InlineData(new[] { OperationOutcomeStatus.Succeeded, OperationOutcomeStatus.Failed }, OperationOutcomeStatus.Partial)]
    [InlineData(new[] { OperationOutcomeStatus.Succeeded, OperationOutcomeStatus.Cancelled }, OperationOutcomeStatus.Partial)]
    [InlineData(new[] { OperationOutcomeStatus.Partial, OperationOutcomeStatus.Succeeded }, OperationOutcomeStatus.Partial)]
    [InlineData(new[] { OperationOutcomeStatus.Blocked, OperationOutcomeStatus.Failed }, OperationOutcomeStatus.Failed)]
    [InlineData(new[] { OperationOutcomeStatus.Blocked, OperationOutcomeStatus.Cancelled }, OperationOutcomeStatus.Blocked)]
    // One part of unknown outcome makes the whole unknown: it is never counted as done or as not done.
    [InlineData(new[] { OperationOutcomeStatus.Succeeded, OperationOutcomeStatus.Unknown }, OperationOutcomeStatus.Unknown)]
    [InlineData(new[] { OperationOutcomeStatus.Failed, OperationOutcomeStatus.Unknown, OperationOutcomeStatus.Blocked }, OperationOutcomeStatus.Unknown)]
    public void Statuses_combine_by_one_rule(OperationOutcomeStatus[] parts, OperationOutcomeStatus expected)
        => Assert.Equal(expected, OperationOutcomeAggregate.Of(parts));

    [Fact]
    public void A_run_refused_before_it_reached_a_target_reports_its_own_status_and_reason()
    {
        var outcome = new OperationOutcome(OperationOutcome.NewOperationId(), "RemoveFeature", 7, 12, [])
        {
            OverallStatus = OperationOutcomeStatus.Blocked,
            OverallReasonCode = OperationReasonCodes.WorkerUnavailable,
            Message = "The Worker is not connected.",
        };

        Assert.Equal(OperationOutcomeStatus.Blocked, outcome.Status);
        Assert.True(outcome.NeedsAttention);
        Assert.Equal(32, outcome.OperationId.Length);
    }

    [Fact]
    public void A_complete_run_needs_no_attention()
    {
        var outcome = new OperationOutcome("op-1", "RemoveFeature", 7, 12,
        [
            new OperationTargetOutcome(
                new OperationTarget(OperationTargetKind.Repository, "source-a", RepositoryId: 1, WorkspaceRepositoryId: 1),
                [OperationStepOutcome.Succeeded(Worktree), OperationStepOutcome.Skipped(RemoteBranch), OperationStepOutcome.NoOp(LocalBranch)]),
        ]);

        Assert.Equal(OperationOutcomeStatus.Succeeded, outcome.Status);
        Assert.False(outcome.NeedsAttention);
    }

    private static OperationOutcome PartialRemoval()
    {
        var targets = new List<OperationTargetOutcome>();
        for (var i = 1; i <= 36; i++)
        {
            targets.Add(new OperationTargetOutcome(
                new OperationTarget(OperationTargetKind.Repository, $"clean-{i}", RepositoryId: i, WorkspaceRepositoryId: i, Path: $@"C:\features\x\clean-{i}"),
                [
                    OperationStepOutcome.Succeeded(Worktree, observed: OperationFactGroups.WorktreeRegistration),
                    OperationStepOutcome.Succeeded(LocalBranch, observed: OperationFactGroups.LocalBranches),
                    OperationStepOutcome.Skipped(RemoteBranch),
                ]));
        }

        // Removed, but its branch has commits that are not merged: the branch is kept and that is said.
        targets.Add(new OperationTargetOutcome(
            new OperationTarget(OperationTargetKind.Repository, "source-c", RepositoryId: 37, WorkspaceRepositoryId: 37),
            [
                OperationStepOutcome.Succeeded(Worktree, observed: OperationFactGroups.WorktreeRegistration),
                OperationStepOutcome.Skipped(LocalBranch, OperationReasonCodes.UnmergedCommits, "The branch has 2 commits that are not merged; it was kept."),
            ]));
        targets.Add(new OperationTargetOutcome(
            new OperationTarget(OperationTargetKind.Repository, "source-a", RepositoryId: 38, WorkspaceRepositoryId: 38),
            [
                OperationStepOutcome.Blocked(
                    Worktree, OperationReasonCodes.UncommittedChanges, "3 modified files.", OperationAuthorizations.DiscardUncommittedChanges)
                    with { Observed = OperationFactGroups.WorktreeRegistration | OperationFactGroups.WorkingTree },
                OperationStepOutcome.Skipped(LocalBranch, OperationReasonCodes.PrerequisiteNotMet),
            ]));
        targets.Add(new OperationTargetOutcome(
            new OperationTarget(OperationTargetKind.Repository, "source-b", RepositoryId: 39, WorkspaceRepositoryId: 39),
            [
                OperationStepOutcome.Blocked(Worktree, OperationReasonCodes.WorktreeLocked, "The worktree is locked.", OperationAuthorizations.UnlockWorktree),
                OperationStepOutcome.Skipped(LocalBranch, OperationReasonCodes.PrerequisiteNotMet),
            ]));
        targets.Add(new OperationTargetOutcome(
            new OperationTarget(OperationTargetKind.Repository, "workspace-root", RepositoryId: 100, WorkspaceRepositoryId: 100),
            [
                OperationStepOutcome.Blocked(Worktree, OperationReasonCodes.WaitingForOtherTargets, "2 repositories inside it are not removed yet."),
            ]));

        return new OperationOutcome("0123456789abcdef0123456789abcdef", "RemoveFeature", WorkspaceId: 7, ContextId: 12, targets)
        {
            ContextName = "feature/x",
            DerivedState = DerivedStateReadiness.Pending,
        };
    }
}
