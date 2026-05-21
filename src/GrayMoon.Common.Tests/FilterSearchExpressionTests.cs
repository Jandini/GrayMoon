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
    public void Highlight_segments_include_operators_and_fields()
    {
        var segments = FilterSearchExpression.GetHighlightSegments("api or (topic:web and gray)");
        Assert.Contains(segments, s => s.Kind == FilterSearchHighlightKind.OperatorOr);
        Assert.Contains(segments, s => s.Kind == FilterSearchHighlightKind.OperatorAnd);
        Assert.Contains(segments, s => s.Kind == FilterSearchHighlightKind.Paren);
        Assert.Contains(segments, s => s.Kind == FilterSearchHighlightKind.FieldPrefix && s.Text == "topic:");
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
