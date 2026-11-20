using GrayMoon.Common.Search;

namespace GrayMoon.Common.Tests;

public class FilterSearchExpressionTests
{
    private readonly TestRepository _repo = new(
        Name: "graymoon-api",
        Owner: "acme",
        Topics: "blazor,angular",
        Connector: "github-prod");

    private bool MatchTerm(FilterSearchTerm term) => _repo.Matches(term);

    [Fact]
    public void Empty_query_matches_all()
    {
        Assert.True(FilterSearchExpression.Matches(null, MatchTerm));
        Assert.True(FilterSearchExpression.Matches("   ", MatchTerm));
    }

    [Fact]
    public void Implicit_and_requires_both_terms()
    {
        Assert.True(FilterSearchExpression.Matches("gray api", MatchTerm));
        Assert.False(FilterSearchExpression.Matches("gray zz", MatchTerm));
    }

    [Fact]
    public void Or_matches_either_term()
    {
        Assert.True(FilterSearchExpression.Matches("zz or api", MatchTerm));
        Assert.False(FilterSearchExpression.Matches("zz or qq", MatchTerm));
    }

    [Fact]
    public void And_binds_tighter_than_or()
    {
        Assert.True(FilterSearchExpression.Matches("zz or api and gray", MatchTerm));
        Assert.False(FilterSearchExpression.Matches("zz or api and qq", MatchTerm));
    }

    [Fact]
    public void Parentheses_override_precedence()
    {
        Assert.True(FilterSearchExpression.Matches("(zz or api) and gray", MatchTerm));
        Assert.False(FilterSearchExpression.Matches("(zz or qq) and gray", MatchTerm));
    }

    [Fact]
    public void Topic_field_token_matches_topics()
    {
        Assert.True(FilterSearchExpression.Matches("topic:blazor", MatchTerm));
        Assert.False(FilterSearchExpression.Matches("topic:zz", MatchTerm));
    }

    [Fact]
    public void Topic_or_topic()
    {
        Assert.True(FilterSearchExpression.Matches("topic:blazor or topic:angular", MatchTerm));
    }

    [Fact]
    public void Invalid_syntax_falls_back_without_throwing()
    {
        var parsed = FilterSearchExpression.Parse("(api");
        Assert.False(parsed.IsValid);
        var ex = Record.Exception(() => FilterSearchExpression.Matches("(api", MatchTerm));
        Assert.Null(ex);
        Assert.True(FilterSearchExpression.Matches("gray api", MatchTerm));
    }

    [Fact]
    public void Unbalanced_paren_is_invalid_parse()
    {
        var parsed = FilterSearchExpression.Parse("(api or web");
        Assert.False(parsed.IsValid);
    }

    [Fact]
    public void Nesting_up_to_the_limit_parses()
    {
        var depth = FilterSearchExpression.MaxNestingDepth;
        var query = new string('(', depth) + "api" + new string(')', depth);

        Assert.True(FilterSearchExpression.Parse(query).IsValid);
        Assert.True(FilterSearchExpression.Matches(query, MatchTerm));
    }

    /// <summary>
    /// The parser recurses once per parenthesis level. Before the limit, a long run of <c>(</c> typed or
    /// pasted into a filter box overflowed the stack, which ends the whole process (it cannot be caught).
    /// </summary>
    [Fact]
    public void Nesting_past_the_limit_is_invalid_and_does_not_overflow_the_stack()
    {
        var justOver = FilterSearchExpression.MaxNestingDepth + 1;
        Assert.False(FilterSearchExpression.Parse(
            new string('(', justOver) + "api" + new string(')', justOver)).IsValid);

        var huge = new string('(', 200_000) + "api";
        Assert.False(FilterSearchExpression.Parse(huge).IsValid);
        // Invalid syntax falls back to word matching, as for any other parse error.
        Assert.False(FilterSearchExpression.Matches(huge, MatchTerm));
    }

    /// <summary>A grid matches every row against one query; it is parsed once, not once per row.</summary>
    [Fact]
    public void Repeated_parse_of_the_same_query_returns_the_cached_result()
    {
        var query = "cached-" + Guid.NewGuid().ToString("N") + " or api";

        var first = FilterSearchExpression.Parse(query);
        var second = FilterSearchExpression.Parse(new string(query.AsSpan()));

        Assert.True(first.IsValid);
        Assert.Same(first, second);
    }

    private sealed record TestRepository(string Name, string Owner, string Topics, string Connector)
    {
        public bool Matches(FilterSearchTerm term)
        {
            if (!string.IsNullOrEmpty(term.Field))
            {
                return term.Field switch
                {
                    "topic" => Topics.Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                    _ => true,
                };
            }

            return Name.Contains(term.Value, StringComparison.OrdinalIgnoreCase)
                   || Owner.Contains(term.Value, StringComparison.OrdinalIgnoreCase)
                   || Topics.Contains(term.Value, StringComparison.OrdinalIgnoreCase)
                   || Connector.Contains(term.Value, StringComparison.OrdinalIgnoreCase);
        }
    }
}
