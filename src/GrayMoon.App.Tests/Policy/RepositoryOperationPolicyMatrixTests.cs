using GrayMoon.Application.Repositories.Policy;

namespace GrayMoon.App.Tests.Policy;

/// <summary>
/// The whole rule table, written out a second time by hand. The tag answers come from what GrayMoon did for a tag
/// checkout before the policy existed (the characterization tests); the reference answers come from the design
/// ("Allowed", "Refused or skipped"), not from the table under test.
/// </summary>
public sealed class RepositoryOperationPolicyMatrixTests
{
    /// <summary>Operations that a tag checkout forbids, in a page or in a service.</summary>
    private static readonly HashSet<RepositoryOperation> ForbiddenOnTag =
    [
        RepositoryOperation.SyncFromRow,
        RepositoryOperation.SyncLevel,
        RepositoryOperation.RefreshProjects,
        RepositoryOperation.RestorePackages,
        RepositoryOperation.Pull,
        RepositoryOperation.ReturnToDefault,
        RepositoryOperation.ReturnToDefaultCleanup,
        RepositoryOperation.UpdateBranchFromDefault,
        RepositoryOperation.UpdateDependencies,
        RepositoryOperation.UpdateVersionFiles,
        RepositoryOperation.Commit,
        RepositoryOperation.Push,
        RepositoryOperation.UndoPush,
        RepositoryOperation.CreatePullRequest,
        RepositoryOperation.MergePullRequest,
        RepositoryOperation.CreateFeatureBranch,
    ];

    /// <summary>What a reference repository on a branch still allows in a bulk action.</summary>
    private static readonly HashSet<RepositoryOperation> ReferenceAllowsInBulk =
    [
        RepositoryOperation.Sync,
        RepositoryOperation.SyncFromRow,
        RepositoryOperation.SyncLevel,
        RepositoryOperation.Fetch,
        RepositoryOperation.RefreshProjects,
        RepositoryOperation.RestorePackages,
        RepositoryOperation.Pull,
        RepositoryOperation.ManualCheckout,
        RepositoryOperation.DeleteLocalBranch,
    ];

    /// <summary>What it allows in addition when the user names the repository.</summary>
    private static readonly HashSet<RepositoryOperation> ReferenceAllowsForOneOnly =
    [
        RepositoryOperation.ReturnToDefault,
        RepositoryOperation.Stage,
        RepositoryOperation.Unstage,
        RepositoryOperation.Discard,
    ];

    /// <summary>What a reference snapshot in a Feature still allows in a bulk action.</summary>
    private static readonly HashSet<RepositoryOperation> SnapshotAllowsInBulk =
    [
        RepositoryOperation.Sync,
        RepositoryOperation.SyncFromRow,
        RepositoryOperation.SyncLevel,
        RepositoryOperation.Fetch,
        RepositoryOperation.RefreshProjects,
        RepositoryOperation.RestorePackages,
    ];

    /// <summary>What it allows in addition when the user names the repository.</summary>
    private static readonly HashSet<RepositoryOperation> SnapshotAllowsForOneOnly =
    [
        RepositoryOperation.Stage,
        RepositoryOperation.Unstage,
        RepositoryOperation.Discard,
    ];

    public static TheoryData<RepositoryOperation, RepositoryOperationScope, bool, bool, bool> TagMatrix()
    {
        var data = new TheoryData<RepositoryOperation, RepositoryOperationScope, bool, bool, bool>();
        foreach (var operation in Enum.GetValues<RepositoryOperation>())
        foreach (var scope in Enum.GetValues<RepositoryOperationScope>())
        foreach (var onTag in new[] { false, true })
        foreach (var isWorkspaceRepository in new[] { false, true })
        foreach (var isFeatureContext in new[] { false, true })
            data.Add(operation, scope, onTag, isWorkspaceRepository, isFeatureContext);
        return data;
    }

    public static TheoryData<RepositoryOperation, RepositoryOperationScope, bool, bool, bool, bool> ReferenceMatrix()
    {
        var data = new TheoryData<RepositoryOperation, RepositoryOperationScope, bool, bool, bool, bool>();
        foreach (var operation in Enum.GetValues<RepositoryOperation>())
        foreach (var scope in Enum.GetValues<RepositoryOperationScope>())
        foreach (var onTag in new[] { false, true })
        foreach (var isWorkspaceRepository in new[] { false, true })
        foreach (var isReference in new[] { false, true })
        foreach (var isReferenceSnapshot in new[] { false, true })
            data.Add(operation, scope, onTag, isWorkspaceRepository, isReference, isReferenceSnapshot);
        return data;
    }

    [Theory]
    [MemberData(nameof(TagMatrix))]
    public void A_writable_repository_answers_as_it_did_before_the_policy(
        RepositoryOperation operation,
        RepositoryOperationScope scope,
        bool onTag,
        bool isWorkspaceRepository,
        bool isFeatureContext)
    {
        var subject = RepositoryPolicySubject.ForCheckout(
            onTag ? null : "feature/x",
            onTag ? "v1.0.0" : null,
            isWorkspaceRepository,
            isFeatureContext);

        var decision = RepositoryOperationPolicy.Evaluate(operation, scope, subject);

        // Neither the role nor the context changes a tag rule.
        if (onTag && ForbiddenOnTag.Contains(operation))
            AssertForbidden(decision, scope, RepositoryOperationReason.OnTag);
        else
            AssertAllowed(decision);

        Assert.Equal(decision.IsAllowed, RepositoryOperationPolicy.IsAllowed(operation, subject, scope));
    }

    [Theory]
    [MemberData(nameof(ReferenceMatrix))]
    public void A_reference_repository_answers_as_the_design_says(
        RepositoryOperation operation,
        RepositoryOperationScope scope,
        bool onTag,
        bool isWorkspaceRepository,
        bool isReference,
        bool isReferenceSnapshot)
    {
        var subject = new RepositoryPolicySubject(
            isWorkspaceRepository,
            isReference,
            onTag ? null : "main",
            onTag ? "v1.0.0" : null,
            IsFeatureContext: isReferenceSnapshot,
            isReferenceSnapshot);

        var decision = RepositoryOperationPolicy.Evaluate(operation, scope, subject);

        // A tag is the stricter rule and is reported first, for every role.
        if (onTag && ForbiddenOnTag.Contains(operation))
        {
            AssertForbidden(decision, scope, RepositoryOperationReason.OnTag);
            return;
        }

        // The Workspace repository is never a reference repository, whatever its flags say.
        if (isWorkspaceRepository)
        {
            AssertAllowed(decision);
            return;
        }

        var individual = scope == RepositoryOperationScope.Individual;
        if (isReferenceSnapshot
            && !SnapshotAllowsInBulk.Contains(operation)
            && !(individual && SnapshotAllowsForOneOnly.Contains(operation)))
        {
            AssertForbidden(decision, scope, RepositoryOperationReason.ReferenceSnapshot);
            Assert.Equal("Repository is a read only snapshot in this Feature.", decision.Message);
            return;
        }

        if (isReference
            && !ReferenceAllowsInBulk.Contains(operation)
            && !(individual && ReferenceAllowsForOneOnly.Contains(operation)))
        {
            AssertForbidden(decision, scope, RepositoryOperationReason.Reference);
            Assert.Equal("Repository is read only.", decision.Message);
            return;
        }

        AssertAllowed(decision);
    }

    [Fact]
    public void Every_operation_has_a_rule_row()
    {
        var missing = Enum.GetValues<RepositoryOperation>().Where(operation => !RepositoryOperationPolicy.Rules.ContainsKey(operation));

        Assert.Empty(missing);
    }

    [Fact]
    public void The_rule_table_has_no_row_for_an_unknown_operation()
    {
        Assert.Equal(Enum.GetValues<RepositoryOperation>().Length, RepositoryOperationPolicy.Rules.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => RepositoryOperationPolicy.Evaluate(
            (RepositoryOperation)int.MaxValue,
            RepositoryOperationScope.Bulk,
            RepositoryPolicySubject.ForCheckout("main", null)));
    }

    [Fact]
    public void Individual_scope_never_skips_and_bulk_scope_never_refuses()
    {
        RepositoryPolicySubject[] subjects =
        [
            RepositoryPolicySubject.ForCheckout(null, "v1.0.0"),
            RepositoryPolicySubject.ForCheckout("main", null, isReference: true),
            RepositoryPolicySubject.ForCheckout(null, null, isFeatureContext: true, isReference: true, isReferenceSnapshot: true),
        ];
        foreach (var subject in subjects)
        foreach (var operation in Enum.GetValues<RepositoryOperation>())
        {
            Assert.NotEqual(
                RepositoryOperationOutcome.Skipped,
                RepositoryOperationPolicy.Evaluate(operation, RepositoryOperationScope.Individual, subject).Outcome);
            Assert.NotEqual(
                RepositoryOperationOutcome.Refused,
                RepositoryOperationPolicy.Evaluate(operation, RepositoryOperationScope.Bulk, subject).Outcome);
        }
    }

    /// <summary>A snapshot is never less strict than a reference repository on a branch, so the order of the two checks cannot hide a "no".</summary>
    [Fact]
    public void A_snapshot_never_allows_what_a_reference_repository_refuses()
    {
        foreach (var (operation, rule) in RepositoryOperationPolicy.Rules)
        foreach (var scope in Enum.GetValues<RepositoryOperationScope>())
        {
            if (RepositoryOperationPolicy.Permits(rule.ReferenceSnapshot, scope))
                Assert.True(RepositoryOperationPolicy.Permits(rule.Reference, scope), $"{operation} {scope}");
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("\t", false)]
    [InlineData("v1.0.0", true)]
    public void Only_a_tag_with_text_counts_as_a_tag(string? checkedOutTag, bool expected)
    {
        var subject = RepositoryPolicySubject.ForCheckout("main", checkedOutTag);

        Assert.Equal(expected, subject.IsOnTag);
        Assert.Equal(!expected, RepositoryOperationPolicy.IsAllowed(RepositoryOperation.Push, subject));
    }

    [Fact]
    public void The_tag_messages_are_the_ones_users_already_see()
    {
        var onTag = RepositoryPolicySubject.ForCheckout(null, "v1.0.0");

        Assert.Equal(
            "Repository is pinned to a tag. Checkout a branch before pushing.",
            RepositoryOperationPolicy.Evaluate(RepositoryOperation.Push, RepositoryOperationScope.Individual, onTag).Message);
        Assert.Equal(
            "Repository is on a tag; checkout a branch first.",
            RepositoryOperationPolicy.Evaluate(RepositoryOperation.UpdateDependencies, RepositoryOperationScope.Individual, onTag).Message);
    }

    private static void AssertAllowed(RepositoryOperationDecision decision)
    {
        Assert.Equal(RepositoryOperationOutcome.Allowed, decision.Outcome);
        Assert.Equal(RepositoryOperationReason.None, decision.Reason);
        Assert.Null(decision.Message);
    }

    private static void AssertForbidden(RepositoryOperationDecision decision, RepositoryOperationScope scope, RepositoryOperationReason reason)
    {
        Assert.Equal(
            scope == RepositoryOperationScope.Individual ? RepositoryOperationOutcome.Refused : RepositoryOperationOutcome.Skipped,
            decision.Outcome);
        Assert.Equal(reason, decision.Reason);
        Assert.False(string.IsNullOrWhiteSpace(decision.Message));
    }
}
