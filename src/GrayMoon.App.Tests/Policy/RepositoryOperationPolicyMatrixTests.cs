using GrayMoon.Application.Repositories.Policy;

namespace GrayMoon.App.Tests.Policy;

/// <summary>
/// The whole rule table, written out a second time by hand. The expected answers come from what GrayMoon did for a
/// tag checkout before the policy existed (the characterization tests), not from the table under test.
/// </summary>
public sealed class RepositoryOperationPolicyMatrixTests
{
    /// <summary>Operations that a tag checkout forbids today, in a page or in a service.</summary>
    private static readonly HashSet<RepositoryOperation> ForbiddenOnTag =
    [
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

    public static TheoryData<RepositoryOperation, RepositoryOperationScope, bool, bool, bool> Matrix()
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

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Every_operation_answers_as_it_did_before_the_policy(
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

        // Neither the role nor the context changes a tag rule today.
        if (onTag && ForbiddenOnTag.Contains(operation))
        {
            Assert.Equal(
                scope == RepositoryOperationScope.Individual ? RepositoryOperationOutcome.Refused : RepositoryOperationOutcome.Skipped,
                decision.Outcome);
            Assert.Equal(RepositoryOperationReason.OnTag, decision.Reason);
            Assert.False(string.IsNullOrWhiteSpace(decision.Message));
        }
        else
        {
            Assert.Equal(RepositoryOperationOutcome.Allowed, decision.Outcome);
            Assert.Equal(RepositoryOperationReason.None, decision.Reason);
            Assert.Null(decision.Message);
        }

        Assert.Equal(decision.IsAllowed, RepositoryOperationPolicy.IsAllowed(operation, subject));
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
        var onTag = RepositoryPolicySubject.ForCheckout(null, "v1.0.0");
        foreach (var operation in Enum.GetValues<RepositoryOperation>())
        {
            Assert.NotEqual(
                RepositoryOperationOutcome.Skipped,
                RepositoryOperationPolicy.Evaluate(operation, RepositoryOperationScope.Individual, onTag).Outcome);
            Assert.NotEqual(
                RepositoryOperationOutcome.Refused,
                RepositoryOperationPolicy.Evaluate(operation, RepositoryOperationScope.Bulk, onTag).Outcome);
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
    public void The_messages_are_the_ones_users_already_see()
    {
        var onTag = RepositoryPolicySubject.ForCheckout(null, "v1.0.0");

        Assert.Equal(
            "Repository is pinned to a tag. Checkout a branch before pushing.",
            RepositoryOperationPolicy.Evaluate(RepositoryOperation.Push, RepositoryOperationScope.Individual, onTag).Message);
        Assert.Equal(
            "Repository is on a tag; checkout a branch first.",
            RepositoryOperationPolicy.Evaluate(RepositoryOperation.UpdateDependencies, RepositoryOperationScope.Individual, onTag).Message);
    }

    [Fact]
    public void Reference_fields_do_not_change_an_answer_yet()
    {
        var reference = new RepositoryPolicySubject(
            IsWorkspaceRepository: false, IsReference: true, "main", null, IsFeatureContext: true, IsReferenceSnapshot: true);

        foreach (var operation in Enum.GetValues<RepositoryOperation>())
            Assert.True(RepositoryOperationPolicy.IsAllowed(operation, reference), operation.ToString());
    }
}
