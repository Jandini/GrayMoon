using System.Linq.Expressions;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Policy;
using GrayMoon.Application.Repositories.Policy;
using Microsoft.EntityFrameworkCore;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy;

/// <summary>
/// The query form of the policy must give the answer the policy gives for a bulk action, for every operation and
/// every subject, both as an expression and as SQL run by the database.
/// </summary>
public sealed class RepositoryOperationPolicyExpressionsTests
{
    public static TheoryData<RepositoryOperation, string?, bool, bool, bool> Subjects()
    {
        var data = new TheoryData<RepositoryOperation, string?, bool, bool, bool>();
        foreach (var operation in Enum.GetValues<RepositoryOperation>())
        foreach (var tag in new[] { null, "", "v1.0.0" })
        foreach (var isWorkspaceRepository in new[] { false, true })
        foreach (var isReference in new[] { false, true })
        foreach (var isReferenceSnapshot in new[] { false, true })
            data.Add(operation, tag, isWorkspaceRepository, isReference, isReferenceSnapshot);
        return data;
    }

    public static TheoryData<RepositoryOperation, string?, bool> StoredLinks()
    {
        var data = new TheoryData<RepositoryOperation, string?, bool>();
        foreach (var operation in Enum.GetValues<RepositoryOperation>())
        foreach (var tag in new[] { null, "", "v1.0.0" })
        foreach (var isReference in new[] { false, true })
            data.Add(operation, tag, isReference);
        return data;
    }

    [Theory]
    [MemberData(nameof(Subjects))]
    public void The_expanded_expression_agrees_with_the_policy(
        RepositoryOperation operation,
        string? storedTag,
        bool isWorkspaceRepository,
        bool isReference,
        bool isReferenceSnapshot)
    {
        var role = isWorkspaceRepository ? WorkspaceRepositoryRole.Workspace : WorkspaceRepositoryRole.Source;
        Expression<Func<string?, WorkspaceRepositoryRole, bool, bool, bool>> query = (tag, r, reference, snapshot) =>
            RepositoryOperationPolicyExpressions.Allows(operation, tag, r, reference, snapshot);
        var expanded = (Expression<Func<string?, WorkspaceRepositoryRole, bool, bool, bool>>)RepositoryOperationPolicyExpressions.Expand(query);

        var expected = RepositoryOperationPolicy.IsAllowed(
            operation,
            RepositoryPolicySubject.ForCheckout("main", storedTag, isWorkspaceRepository, isReferenceSnapshot, isReference, isReferenceSnapshot));

        Assert.DoesNotContain(nameof(RepositoryOperationPolicyExpressions), expanded.ToString());
        Assert.Equal(expected, expanded.Compile()(storedTag, role, isReference, isReferenceSnapshot));
        Assert.Equal(expected, RepositoryOperationPolicyExpressions.Allows(operation, storedTag, role, isReference, isReferenceSnapshot));
    }

    [Theory]
    [MemberData(nameof(StoredLinks))]
    public async Task The_database_agrees_with_the_policy(RepositoryOperation operation, string? storedTag, bool isReference)
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ProducerId, link =>
        {
            link.CheckedOutTag = storedTag;
            link.IsReference = isReference;
        });
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();

        var allowed = await db.WorkspaceRepositories
            .Where(wr => wr.RepositoryId == graph.ProducerId)
            .Where(wr => RepositoryOperationPolicyExpressions.Allows(operation, wr.CheckedOutTag, wr.Role, wr.IsReference, false))
            .ExpandRepositoryPolicy()
            .AnyAsync();
        var onTag = await db.WorkspaceRepositories
            .Where(wr => wr.RepositoryId == graph.ProducerId)
            .GroupBy(_ => 1)
            .Select(g => g.Count(wr => RepositoryOperationPolicyExpressions.IsOnTag(wr.CheckedOutTag)))
            .ExpandRepositoryPolicy()
            .FirstAsync();

        var subject = RepositoryPolicySubject.ForCheckout("main", storedTag, isReference: isReference);
        Assert.Equal(RepositoryOperationPolicy.IsAllowed(operation, subject), allowed);
        Assert.Equal(subject.IsOnTag ? 1 : 0, onTag);
    }

    [Fact]
    public void An_operation_without_a_rule_cannot_be_put_into_a_query()
    {
        var unknown = (RepositoryOperation)int.MaxValue;
        Expression<Func<string?, bool>> query = tag =>
            RepositoryOperationPolicyExpressions.Allows(unknown, tag, WorkspaceRepositoryRole.Source, false, false);

        Assert.Throws<ArgumentOutOfRangeException>(() => RepositoryOperationPolicyExpressions.Expand(query));
    }

    /// <summary>
    /// The one place the two forms differ: the policy does not count blank text as a tag, the column comparison does.
    /// Nothing stores blank text as a tag, and the counters compared the column this way before the policy existed.
    /// </summary>
    [Fact]
    public void Blank_text_is_a_tag_to_the_query_form_only()
    {
        Assert.True(RepositoryOperationPolicyExpressions.IsOnTag(" "));
        Assert.False(RepositoryPolicySubject.ForCheckout("main", " ").IsOnTag);
    }
}
