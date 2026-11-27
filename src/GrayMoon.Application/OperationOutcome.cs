using System.Text.Json.Serialization;

namespace GrayMoon.Application;

/// <summary>
/// How one step, one target or a whole operation ended. A caller (a page, a REST client, a future MCP tool) decides
/// from this value and the reason code, never from message text.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OperationOutcomeStatus>))]
public enum OperationOutcomeStatus
{
    /// <summary>The work was done and its result was verified.</summary>
    Succeeded = 0,

    /// <summary>Nothing had to be done: the wanted state was already there.</summary>
    NoOp = 1,

    /// <summary>Left out on purpose (not requested, not applicable, excluded by policy). Not a failure.</summary>
    Skipped = 2,

    /// <summary>Refused before anything changed, for a reason the caller can act on (uncommitted work, a lock, policy, a missing authorization).</summary>
    Blocked = 3,

    /// <summary>Attempted and verified as not done.</summary>
    Failed = 4,

    /// <summary>Stopped by the caller or by shutdown before it ran or finished; what it changed is reported by the other steps.</summary>
    Cancelled = 5,

    /// <summary>
    /// Whether it happened is not known (a lost reply, a transport failure after the command was sent). Never to be
    /// treated as "not done": the affected facts are read again before anything is retried.
    /// </summary>
    Unknown = 6,

    /// <summary>Aggregate only: some parts succeeded and others did not.</summary>
    Partial = 7,
}

/// <summary>Whether the state derived from what an operation changed (versions, dependency levels, file checks) is up to date.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DerivedStateReadiness>))]
public enum DerivedStateReadiness
{
    /// <summary>The operation changed nothing that derived state is computed from.</summary>
    NotAffected = 0,

    /// <summary>Derived state was recomputed before the result was returned.</summary>
    Ready = 1,

    /// <summary>A recompute is still due; values read now may be the old ones.</summary>
    Pending = 2,
}

/// <summary>What an operation target is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OperationTargetKind>))]
public enum OperationTargetKind
{
    Repository = 0,
    Context = 1,
    Workspace = 2,
}

/// <summary>
/// The groups of repository facts an operation can observe or invalidate. A step names the groups its result
/// established (<see cref="OperationStepOutcome.Observed"/>) and the ones it made stale without reading them
/// (<see cref="OperationStepOutcome.Invalidated"/>), so a consumer knows what it may rely on.
/// </summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<OperationFactGroups>))]
public enum OperationFactGroups
{
    None = 0,

    /// <summary>Whether the worktree is registered and where.</summary>
    WorktreeRegistration = 1 << 0,

    /// <summary>What is checked out: branch, tag, HEAD commit.</summary>
    Identity = 1 << 1,

    /// <summary>The local branch inventory of the shared repository.</summary>
    LocalBranches = 1 << 2,

    /// <summary>The remote branch and tag inventory as last fetched.</summary>
    RemoteRefs = 1 << 3,

    /// <summary>Index and working tree changes.</summary>
    WorkingTree = 1 << 4,

    /// <summary>Upstream flag and the ahead and behind counts.</summary>
    Divergence = 1 << 5,

    /// <summary>The calculated repository version and the effective version decision.</summary>
    Version = 1 << 6,

    /// <summary>Discovered projects and package references.</summary>
    Projects = 1 << 7,

    /// <summary>Dependency levels, matched and unmatched dependencies, file-version lines.</summary>
    DerivedGraph = 1 << 8,

    /// <summary>Pull request and CI state.</summary>
    PullRequest = 1 << 9,

    /// <summary>Whether file watchers cover the checkout.</summary>
    WatcherCoverage = 1 << 10,
}

/// <summary>
/// Stable reason codes for <see cref="OperationStepOutcome.ReasonCode"/>. They are part of the contract: a code is
/// never renamed, and new ones are added here. Text for people is in <see cref="OperationStepOutcome.Message"/>.
/// </summary>
public static class OperationReasonCodes
{
    /// <summary>No particular reason: the step did what it was asked.</summary>
    public const string None = "None";

    public const string AlreadySatisfied = "AlreadySatisfied";
    public const string NotRequested = "NotRequested";
    public const string NotApplicable = "NotApplicable";

    /// <summary>Staged, unstaged or untracked work that the step would have discarded.</summary>
    public const string UncommittedChanges = "UncommittedChanges";

    /// <summary>Ignored files that the step would have deleted.</summary>
    public const string IgnoredFiles = "IgnoredFiles";

    /// <summary>A merge, rebase or cherry-pick that has not finished.</summary>
    public const string OperationInProgress = "OperationInProgress";

    /// <summary>Commits that no branch or remote holds.</summary>
    public const string UnpreservedCommits = "UnpreservedCommits";

    /// <summary>Another Git repository inside the folder that the step would have deleted with it.</summary>
    public const string NestedRepository = "NestedRepository";

    public const string WorktreeLocked = "WorktreeLocked";
    public const string PathInUse = "PathInUse";
    public const string AccessDenied = "AccessDenied";
    public const string NotFound = "NotFound";

    /// <summary>The checkout or ref is not what the operation was approved for.</summary>
    public const string ChangedSinceReview = "ChangedSinceReview";

    /// <summary>The step needs a consent the caller did not give; <see cref="OperationStepOutcome.RequiredAuthorization"/> names it.</summary>
    public const string AuthorizationRequired = "AuthorizationRequired";

    /// <summary>Repository policy (read only, tag pinned, archived, Feature branch rule) excludes the step.</summary>
    public const string PolicyExcluded = "PolicyExcluded";

    /// <summary>The branch has commits that are not merged where they would have to be.</summary>
    public const string UnmergedCommits = "UnmergedCommits";

    /// <summary>The remote ref no longer points at the expected commit.</summary>
    public const string LeaseRefused = "LeaseRefused";

    /// <summary>Another step or target has to finish first (a root worktree waiting for its source worktrees).</summary>
    public const string WaitingForOtherTargets = "WaitingForOtherTargets";

    /// <summary>An earlier step of the same target did not succeed, so this one did not run.</summary>
    public const string PrerequisiteNotMet = "PrerequisiteNotMet";

    public const string WorkerUnavailable = "WorkerUnavailable";
    public const string RemoteUnavailable = "RemoteUnavailable";
    public const string Cancelled = "Cancelled";

    /// <summary>The command was sent and no usable answer came back.</summary>
    public const string OutcomeUnknown = "OutcomeUnknown";

    /// <summary>A failure with no more specific code; the message has the detail.</summary>
    public const string Error = "Error";
}

/// <summary>The consents a destructive step can require. Each is given for named targets, never for "everything".</summary>
public static class OperationAuthorizations
{
    public const string DiscardUncommittedChanges = "DiscardUncommittedChanges";
    public const string DeleteIgnoredFiles = "DeleteIgnoredFiles";
    public const string UnlockWorktree = "UnlockWorktree";
    public const string ForceDeleteLocalBranch = "ForceDeleteLocalBranch";
    public const string DeleteRemoteBranch = "DeleteRemoteBranch";
    public const string DropUnpreservedCommits = "DropUnpreservedCommits";
}

/// <summary>What a step acted on. Ids, not display names, identify it; the name is for people.</summary>
public sealed record OperationTarget(
    OperationTargetKind Kind,
    string Name,
    int? RepositoryId = null,
    int? WorkspaceRepositoryId = null,
    string? Path = null);

/// <summary>
/// One independent step of an operation on one target (for a Feature removal: the worktree, the local branch, the
/// remote branch, leftover files). Each step has its own outcome, so "worktree removed, branch kept" is neither a
/// success of everything nor a failure of everything.
/// </summary>
/// <param name="Step">Stable key of the step, one of the operation's own constants (for example <c>Worktree</c>).</param>
/// <param name="Status">How the step ended. Never <see cref="OperationOutcomeStatus.Partial"/>.</param>
/// <param name="ReasonCode">One of <see cref="OperationReasonCodes"/>.</param>
/// <param name="Message">Text for people: what happened and what to do. Null when the status says it all.</param>
public sealed record OperationStepOutcome(
    string Step,
    OperationOutcomeStatus Status,
    string ReasonCode = OperationReasonCodes.None,
    string? Message = null)
{
    /// <summary>Fact groups this step's result established as current.</summary>
    public OperationFactGroups Observed { get; init; }

    /// <summary>Fact groups this step made stale without reading them again.</summary>
    public OperationFactGroups Invalidated { get; init; }

    /// <summary>
    /// For a <see cref="OperationOutcomeStatus.Blocked"/> step that an explicit consent would let run: the consent,
    /// one of <see cref="OperationAuthorizations"/>. It is given for this target only and after the caller has seen
    /// the current evidence; a retry without it is refused again.
    /// </summary>
    public string? RequiredAuthorization { get; init; }

    /// <summary>The same request may be sent again as it is (after the cause is gone) without any new consent.</summary>
    public bool CanRetry { get; init; }

    public static OperationStepOutcome Succeeded(string step, string? message = null, OperationFactGroups observed = OperationFactGroups.None)
        => new(step, OperationOutcomeStatus.Succeeded, OperationReasonCodes.None, message) { Observed = observed };

    public static OperationStepOutcome NoOp(string step, string? message = null, OperationFactGroups observed = OperationFactGroups.None)
        => new(step, OperationOutcomeStatus.NoOp, OperationReasonCodes.AlreadySatisfied, message) { Observed = observed };

    public static OperationStepOutcome Skipped(string step, string reasonCode = OperationReasonCodes.NotRequested, string? message = null)
        => new(step, OperationOutcomeStatus.Skipped, reasonCode, message);

    public static OperationStepOutcome Blocked(string step, string reasonCode, string? message = null, string? requiredAuthorization = null, bool canRetry = true)
        => new(step, OperationOutcomeStatus.Blocked, reasonCode, message) { RequiredAuthorization = requiredAuthorization, CanRetry = canRetry };

    public static OperationStepOutcome Failed(string step, string? message, string reasonCode = OperationReasonCodes.Error, bool canRetry = true)
        => new(step, OperationOutcomeStatus.Failed, reasonCode, message) { CanRetry = canRetry };

    public static OperationStepOutcome Cancelled(string step, string? message = null)
        => new(step, OperationOutcomeStatus.Cancelled, OperationReasonCodes.Cancelled, message) { CanRetry = true };

    /// <summary>The command may or may not have taken effect; <paramref name="invalidated"/> names what has to be read again first.</summary>
    public static OperationStepOutcome Unknown(string step, string? message, OperationFactGroups invalidated)
        => new(step, OperationOutcomeStatus.Unknown, OperationReasonCodes.OutcomeUnknown, message) { Invalidated = invalidated };
}

/// <summary>Everything an operation did to one target: its steps in the order they apply, and their aggregate.</summary>
public sealed record OperationTargetOutcome(OperationTarget Target, IReadOnlyList<OperationStepOutcome> Steps)
{
    /// <summary>Aggregate of <see cref="Steps"/> (see <see cref="OperationOutcomeAggregate"/>).</summary>
    public OperationOutcomeStatus Status => OperationOutcomeAggregate.Of(Steps.Select(s => s.Status));

    /// <summary>The step with this key, or null when the operation has no such step for the target.</summary>
    public OperationStepOutcome? Step(string step)
        => Steps.FirstOrDefault(s => string.Equals(s.Step, step, StringComparison.Ordinal));
}

/// <summary>
/// The typed outcome of one operation run: which Workspace and context it ran in, what it did to each target step
/// by step, and whether derived state is ready. It is the same object for the UI, REST and a future MCP tool; none
/// of them has to read toast text, colors or logs to know what happened.
/// </summary>
/// <param name="OperationId">
/// Identity of this run, chosen by the caller or generated. A repeated request with the same id must not repeat
/// destructive work; a caller polls or correlates by it.
/// </param>
/// <param name="Operation">Stable name of the operation (for example <c>RemoveFeature</c>).</param>
/// <param name="WorkspaceId">The Workspace the operation ran in.</param>
/// <param name="ContextId">The context (special Workspace or Feature) it ran in; null for an operation on the Workspace as a whole.</param>
/// <param name="Targets">One entry per target, also for targets that were skipped or that succeeded.</param>
public sealed record OperationOutcome(
    string OperationId,
    string Operation,
    int WorkspaceId,
    int? ContextId,
    IReadOnlyList<OperationTargetOutcome> Targets)
{
    /// <summary>Contract version of this shape. Raised only for a change an existing reader could misread.</summary>
    public int ContractVersion { get; init; } = 1;

    /// <summary>Name of the Feature the context belongs to, when it is one. For people; the id identifies it.</summary>
    public string? ContextName { get; init; }

    /// <summary>
    /// How the run as a whole ended when that is not the aggregate of its targets: a run refused before it reached any
    /// target (<see cref="OperationOutcomeStatus.Blocked"/>), cancelled, or of unknown outcome. Null means "the aggregate".
    /// </summary>
    public OperationOutcomeStatus? OverallStatus { get; init; }

    /// <summary>Reason code for <see cref="OverallStatus"/>, one of <see cref="OperationReasonCodes"/>.</summary>
    public string? OverallReasonCode { get; init; }

    /// <summary>Text for people about the run as a whole.</summary>
    public string? Message { get; init; }

    public DerivedStateReadiness DerivedState { get; init; } = DerivedStateReadiness.NotAffected;

    /// <summary><see cref="OverallStatus"/> when set, otherwise the aggregate of the targets.</summary>
    public OperationOutcomeStatus Status => OverallStatus ?? OperationOutcomeAggregate.Of(Targets.Select(t => t.Status));

    /// <summary>
    /// True when anything is left that a caller should look at: a blocked, failed, cancelled or unknown step. A run
    /// whose steps all succeeded, were not needed or were skipped on purpose is complete.
    /// </summary>
    public bool NeedsAttention => Status is not (OperationOutcomeStatus.Succeeded or OperationOutcomeStatus.NoOp or OperationOutcomeStatus.Skipped);

    /// <summary>The target of a repository, or null.</summary>
    public OperationTargetOutcome? ForWorkspaceRepository(int workspaceRepositoryId)
        => Targets.FirstOrDefault(t => t.Target.WorkspaceRepositoryId == workspaceRepositoryId);

    /// <summary>How many targets ended with each status, for a summary line ("37 removed, 2 blocked").</summary>
    public IReadOnlyDictionary<OperationOutcomeStatus, int> CountByStatus()
        => Targets.GroupBy(t => t.Status).ToDictionary(g => g.Key, g => g.Count());

    public static string NewOperationId() => Guid.NewGuid().ToString("N");
}

/// <summary>The one rule for combining step statuses into a target status and target statuses into a run status.</summary>
public static class OperationOutcomeAggregate
{
    /// <summary>
    /// Unknown wins: one part of unknown outcome makes the whole unknown, because nothing may be assumed about it.
    /// Otherwise parts that all ended the same way give that status (skipped parts are ignored next to parts that
    /// did something), and a mixture of done and not-done parts is <see cref="OperationOutcomeStatus.Partial"/>.
    /// Nothing at all is <see cref="OperationOutcomeStatus.NoOp"/>.
    /// </summary>
    public static OperationOutcomeStatus Of(IEnumerable<OperationOutcomeStatus> parts)
    {
        var done = 0;
        var noOp = 0;
        var skipped = 0;
        var blocked = 0;
        var failed = 0;
        var cancelled = 0;
        var partial = 0;
        foreach (var part in parts)
        {
            switch (part)
            {
                case OperationOutcomeStatus.Unknown:
                    return OperationOutcomeStatus.Unknown;
                case OperationOutcomeStatus.Succeeded:
                    done++;
                    break;
                case OperationOutcomeStatus.NoOp:
                    noOp++;
                    break;
                case OperationOutcomeStatus.Skipped:
                    skipped++;
                    break;
                case OperationOutcomeStatus.Blocked:
                    blocked++;
                    break;
                case OperationOutcomeStatus.Failed:
                    failed++;
                    break;
                case OperationOutcomeStatus.Cancelled:
                    cancelled++;
                    break;
                case OperationOutcomeStatus.Partial:
                    partial++;
                    break;
            }
        }

        var notDone = blocked + failed + cancelled;
        if (partial > 0 || (notDone > 0 && done > 0))
            return OperationOutcomeStatus.Partial;
        if (notDone == 0)
            return done > 0 ? OperationOutcomeStatus.Succeeded : noOp > 0 || skipped == 0 ? OperationOutcomeStatus.NoOp : OperationOutcomeStatus.Skipped;
        if (failed == notDone)
            return OperationOutcomeStatus.Failed;
        if (blocked == notDone)
            return OperationOutcomeStatus.Blocked;
        if (cancelled == notDone)
            return OperationOutcomeStatus.Cancelled;

        // Different kinds of "not done" and nothing done: the most actionable one describes the whole.
        return failed > 0 ? OperationOutcomeStatus.Failed : OperationOutcomeStatus.Blocked;
    }
}
