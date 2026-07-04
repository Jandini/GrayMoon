using System.Linq.Expressions;

namespace GrayMoon.Common.Search;

/// <summary>Builds EF-translatable <see cref="Expression"/> filters from <see cref="FilterSearchExpression"/> queries.</summary>
public static class FilterSearchEfQueryable
{
    public static IQueryable<T> ApplySearch<T>(
        this IQueryable<T> query,
        string? searchQuery,
        Func<FilterSearchTerm, Expression<Func<T, bool>>> buildTermPredicate)
    {
        var filter = BuildFilter(searchQuery, buildTermPredicate);
        return filter is null ? query : query.Where(filter);
    }

    public static Expression<Func<T, bool>>? BuildFilter<T>(
        string? searchQuery,
        Func<FilterSearchTerm, Expression<Func<T, bool>>> buildTermPredicate)
    {
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return null;
        }

        var parsed = FilterSearchExpression.Parse(searchQuery);
        if (parsed.IsValid)
        {
            if (parsed.Expression is null)
            {
                return null;
            }

            return BuildNodeExpression(parsed.Expression, buildTermPredicate);
        }

        Expression<Func<T, bool>>? combined = null;
        foreach (var token in searchQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var term = ParseTermToken(token);
            var termExpr = buildTermPredicate(term);
            combined = combined is null ? termExpr : CombineAnd(combined, termExpr);
        }

        return combined;
    }

    private static Expression<Func<T, bool>> BuildNodeExpression<T>(
        FilterSearchNode node,
        Func<FilterSearchTerm, Expression<Func<T, bool>>> buildTermPredicate) =>
        node switch
        {
            FilterSearchTermNode termNode => buildTermPredicate(termNode.Term),
            FilterSearchAndNode andNode => CombineAnd(
                BuildNodeExpression(andNode.Left, buildTermPredicate),
                BuildNodeExpression(andNode.Right, buildTermPredicate)),
            FilterSearchOrNode orNode => CombineOr(
                BuildNodeExpression(orNode.Left, buildTermPredicate),
                BuildNodeExpression(orNode.Right, buildTermPredicate)),
            _ => throw new InvalidOperationException("Unknown filter search node."),
        };

    private static Expression<Func<T, bool>> CombineAnd<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right)
    {
        var param = Expression.Parameter(typeof(T), "x");
        var body = Expression.AndAlso(
            ReplaceParameter(left.Body, left.Parameters[0], param),
            ReplaceParameter(right.Body, right.Parameters[0], param));
        return Expression.Lambda<Func<T, bool>>(body, param);
    }

    private static Expression<Func<T, bool>> CombineOr<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right)
    {
        var param = Expression.Parameter(typeof(T), "x");
        var body = Expression.OrElse(
            ReplaceParameter(left.Body, left.Parameters[0], param),
            ReplaceParameter(right.Body, right.Parameters[0], param));
        return Expression.Lambda<Func<T, bool>>(body, param);
    }

    private static Expression ReplaceParameter(Expression expression, ParameterExpression source, ParameterExpression target) =>
        new ParameterReplacer(source, target).Visit(expression);

    private static FilterSearchTerm ParseTermToken(string token)
    {
        var colon = token.IndexOf(':');
        if (colon > 0)
        {
            var field = token[..colon];
            var value = token[(colon + 1)..];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return new FilterSearchTerm(field.ToLowerInvariant(), value);
            }
        }

        return new FilterSearchTerm(null, token);
    }

    private sealed class ParameterReplacer(ParameterExpression source, ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == source ? target : base.VisitParameter(node);
    }
}
