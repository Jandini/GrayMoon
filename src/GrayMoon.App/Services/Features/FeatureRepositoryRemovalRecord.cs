using System.Text.Json;
using GrayMoon.Application;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// What Remove Feature did to one repository, step by step, stored on its row
/// (<c>WorkspaceFeatureRepository.RemovalOutcomeJson</c>). The removal writes it after every step, so a restart, a
/// lost Worker reply or a failed branch delete never loses what already happened: a retry continues from the steps
/// that are not resolved, and the status of an incomplete removal reads the same record.
/// <para>
/// <see cref="BranchSha"/> and <see cref="BranchHasUpstream"/> are the Feature branch's tip and upstream as read
/// when its delete was first attempted. They are the identity a later retry needs (the lease of the remote delete),
/// because the local branch may be gone by then.
/// </para>
/// </summary>
internal sealed record FeatureRepositoryRemovalRecord
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public List<OperationStepOutcome> Steps { get; init; } = [];
    public string? BranchSha { get; init; }
    public bool? BranchHasUpstream { get; init; }

    /// <summary>The branch the worktree was on instead of its Feature branch (or "(detached commit)"), named in the report.</summary>
    public string? KeptBranchName { get; init; }

    public bool ResidueRemaining { get; init; }
    public int ResidueFileCount { get; init; }
    public List<string>? ResidueSampleFiles { get; init; }
    public string? ResidueMessage { get; init; }

    public OperationStepOutcome? Step(string step)
        => Steps.FirstOrDefault(s => string.Equals(s.Step, step, StringComparison.Ordinal));

    /// <summary>A copy with <paramref name="outcome"/> in place of the earlier outcome of the same step, or added after the others.</summary>
    public FeatureRepositoryRemovalRecord With(OperationStepOutcome outcome)
    {
        var steps = Steps.Where(s => !string.Equals(s.Step, outcome.Step, StringComparison.Ordinal)).ToList();
        steps.Add(outcome);
        steps.Sort((a, b) => Order(a.Step).CompareTo(Order(b.Step)));
        return this with { Steps = steps };
    }

    /// <summary>
    /// True when the step still has to run to an end: it never ran, failed, was cancelled, or its outcome is not
    /// known. A step that was skipped or blocked for a reason is resolved: the branch was kept on purpose.
    /// </summary>
    public bool IsUnresolved(string step)
        => Step(step) is not { } outcome
           || outcome.Status is OperationOutcomeStatus.Failed or OperationOutcomeStatus.Unknown or OperationOutcomeStatus.Cancelled;

    public bool WorktreeRemoved
        => Step(RemoveFeatureSteps.Worktree) is { Status: OperationOutcomeStatus.Succeeded or OperationOutcomeStatus.NoOp };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The stored record, or an empty one when nothing was stored or the text cannot be read (a retry then runs every step).</summary>
    public static FeatureRepositoryRemovalRecord Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new FeatureRepositoryRemovalRecord();

        try
        {
            return JsonSerializer.Deserialize<FeatureRepositoryRemovalRecord>(json, Json) ?? new FeatureRepositoryRemovalRecord();
        }
        catch (JsonException)
        {
            return new FeatureRepositoryRemovalRecord();
        }
    }

    private static int Order(string step) => step switch
    {
        RemoveFeatureSteps.Worktree => 0,
        RemoveFeatureSteps.LocalBranch => 1,
        RemoveFeatureSteps.RemoteBranch => 2,
        RemoveFeatureSteps.Residue => 3,
        _ => 4,
    };
}
