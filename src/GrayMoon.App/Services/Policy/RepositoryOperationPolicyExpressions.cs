using System.Linq.Expressions;
using System.Reflection;
using GrayMoon.App.Models;
using GrayMoon.Application.Repositories.Policy;

namespace GrayMoon.App.Services.Policy;

/// <summary>
/// The policy inside a database query. A query writes <see cref="Allows"/> or <see cref="IsOnTag"/> where it used to
/// compare the tag column itself, and calls <see cref="ExpandRepositoryPolicy{T}"/> before it runs; the calls are then
/// replaced by plain column comparisons built from <see cref="RepositoryOperationPolicy.Rules"/>, so the counters and
/// the services read one rule table.
/// </summary>
public static class RepositoryOperationPolicyExpressions
{
    private static readonly MethodInfo AllowsMethod =
        typeof(RepositoryOperationPolicyExpressions).GetMethod(nameof(Allows))!;

    private static readonly MethodInfo IsOnTagMethod =
        typeof(RepositoryOperationPolicyExpressions).GetMethod(nameof(IsOnTag))!;

    /// <summary>
    /// True when <paramref name="operation"/> may run, as one of many in a bulk action, on a repository with this
    /// stored tag, role and reference state. In a query this becomes column comparisons; a stored tag is never blank
    /// text, so the comparison is with null and the empty string only. The Workspace passes false for
    /// <paramref name="isReferenceSnapshot"/>; a Feature passes its own row's flag.
    /// </summary>
    public static bool Allows(
        RepositoryOperation operation,
        string? checkedOutTag,
        WorkspaceRepositoryRole role,
        bool isReference,
        bool isReferenceSnapshot)
    {
        var rule = RepositoryOperationPolicy.Rules[operation];
        if (!rule.AllowedOnTag && IsOnTag(checkedOutTag))
            return false;

        if (role == WorkspaceRepositoryRole.Workspace)
            return true;

        return (!isReferenceSnapshot || RepositoryOperationPolicy.Permits(rule.ReferenceSnapshot, RepositoryOperationScope.Bulk))
            && (!isReference || RepositoryOperationPolicy.Permits(rule.Reference, RepositoryOperationScope.Bulk));
    }

    /// <summary>True when the stored tag says the repository is on a tag (null and the empty string are not a tag).</summary>
    public static bool IsOnTag(string? checkedOutTag) => !string.IsNullOrEmpty(checkedOutTag);

    /// <summary>Replaces every <see cref="Allows"/> and <see cref="IsOnTag"/> call in the query with column comparisons.</summary>
    public static IQueryable<T> ExpandRepositoryPolicy<T>(this IQueryable<T> query) =>
        query.Provider.CreateQuery<T>(new Expander().Visit(query.Expression));

    /// <summary>The same replacement for one expression; the parity tests use it to compare the query form with the policy.</summary>
    internal static Expression Expand(Expression expression) => new Expander().Visit(expression);

    private sealed class Expander : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method == AllowsMethod)
            {
                var operation = (RepositoryOperation)Expression.Lambda(node.Arguments[0]).Compile().DynamicInvoke()!;
                if (!RepositoryOperationPolicy.Rules.TryGetValue(operation, out var rule))
                    throw new ArgumentOutOfRangeException(nameof(node), operation, "No policy rule for this operation.");

                var allowed = rule.AllowedOnTag
                    ? (Expression)Expression.Constant(true)
                    : Expression.Not(OnTag(Visit(node.Arguments[1])));

                // Terms the rule table allows are left out, so a query pays only for the columns that can say no.
                Expression? reference = null;
                if (!RepositoryOperationPolicy.Permits(rule.ReferenceSnapshot, RepositoryOperationScope.Bulk))
                    reference = Expression.Not(Visit(node.Arguments[4]));
                if (!RepositoryOperationPolicy.Permits(rule.Reference, RepositoryOperationScope.Bulk))
                {
                    var notReference = Expression.Not(Visit(node.Arguments[3]));
                    reference = reference is null ? notReference : Expression.AndAlso(reference, notReference);
                }

                if (reference is null)
                    return allowed;

                var isWorkspaceRepository = Expression.Equal(
                    Visit(node.Arguments[2]),
                    Expression.Constant(WorkspaceRepositoryRole.Workspace));
                var referenceAllows = Expression.OrElse(isWorkspaceRepository, reference);
                return rule.AllowedOnTag ? referenceAllows : Expression.AndAlso(allowed, referenceAllows);
            }

            if (node.Method == IsOnTagMethod)
                return OnTag(Visit(node.Arguments[0]));

            return base.VisitMethodCall(node);
        }

        private static Expression OnTag(Expression tag) =>
            Expression.AndAlso(
                Expression.NotEqual(tag, Expression.Constant(null, typeof(string))),
                Expression.NotEqual(tag, Expression.Constant(string.Empty)));
    }
}
