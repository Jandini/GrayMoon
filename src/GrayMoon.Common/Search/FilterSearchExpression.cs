namespace GrayMoon.Common.Search;

/// <summary>
/// Parses and evaluates boolean filter search queries:
/// implicit AND between terms, explicit <c>and</c>/<c>or</c>, parentheses,
/// and <c>field:value</c> leaves (e.g. <c>topic:blazor</c>).
/// </summary>
public static class FilterSearchExpression
{
    /// <summary>Longest query the filter box accepts (its <c>maxlength</c>).</summary>
    public const int MaxQueryLength = 1000;

    /// <summary>
    /// Deepest parenthesis nesting that parses. The parser and the tree walkers recurse once per level, so
    /// without a limit a long run of <c>(</c> overflows the stack, which ends the process. The filter box
    /// script (<c>wwwroot/js/filterSearchInput.js</c>, <c>MAX_NESTING_DEPTH</c>) uses the same number.
    /// </summary>
    public const int MaxNestingDepth = 64;

    /// <summary>
    /// The last parsed query. A grid matches every row against the same query, so <see cref="Matches"/>
    /// would otherwise tokenize and parse it once per row. The result is immutable.
    /// </summary>
    private static CachedParse? _lastParse;

    public static bool IsValidQuery(string? query) =>
        string.IsNullOrWhiteSpace(query) || Parse(query).IsValid;

    public static FilterSearchParseResult Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return FilterSearchParseResult.Success(null);
        }

        var cached = Volatile.Read(ref _lastParse);
        if (cached is not null && cached.Query == query)
        {
            return cached.Result;
        }

        var result = ParseCore(query);
        Volatile.Write(ref _lastParse, new CachedParse(query, result));
        return result;
    }

    private static FilterSearchParseResult ParseCore(string query)
    {
        try
        {
            var tokens = Tokenize(query);
            var parser = new Parser(tokens);
            var expression = parser.ParseExpression();
            if (!parser.IsAtEnd)
            {
                return FilterSearchParseResult.Failure("Unexpected text after expression.");
            }

            return FilterSearchParseResult.Success(expression);
        }
        catch (ParseException ex)
        {
            return FilterSearchParseResult.Failure(ex.Message);
        }
    }

    public static bool Evaluate(FilterSearchNode node, Func<FilterSearchTerm, bool> matchTerm) =>
        node switch
        {
            FilterSearchTermNode t => matchTerm(t.Term),
            FilterSearchAndNode a => Evaluate(a.Left, matchTerm) && Evaluate(a.Right, matchTerm),
            FilterSearchOrNode o => Evaluate(o.Left, matchTerm) || Evaluate(o.Right, matchTerm),
            _ => false,
        };

    /// <summary>
    /// Returns whether the query matches using the expression parser, or legacy space-AND fallback when invalid.
    /// </summary>
    public static bool Matches(string? query, Func<FilterSearchTerm, bool> matchTerm)
    {
        var parsed = Parse(query);
        if (parsed.IsValid)
        {
            if (parsed.Expression is null)
            {
                return true;
            }

            return Evaluate(parsed.Expression, matchTerm);
        }

        return LegacyAllMatch(query, matchTerm);
    }

    private static bool LegacyAllMatch(string? query, Func<FilterSearchTerm, bool> matchTerm)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!matchTerm(ParseTermToken(token)))
            {
                return false;
            }
        }

        return true;
    }

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

    private static List<Token> Tokenize(string query)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < query.Length)
        {
            var c = query[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c is '(' or ')')
            {
                tokens.Add(new Token(c == '(' ? TokenKind.LParen : TokenKind.RParen, c.ToString(), i));
                i++;
                continue;
            }

            var start = i;
            while (i < query.Length && !char.IsWhiteSpace(query[i]) && query[i] is not '(' and not ')')
            {
                i++;
            }

            var text = query[start..i];
            if (IsOperator(text, "and"))
            {
                tokens.Add(new Token(TokenKind.And, text, start));
            }
            else if (IsOperator(text, "or"))
            {
                tokens.Add(new Token(TokenKind.Or, text, start));
            }
            else
            {
                tokens.Add(new Token(TokenKind.Term, text, start));
            }
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, query.Length));
        return tokens;
    }

    private static bool IsOperator(string text, string op) =>
        text.Equals(op, StringComparison.OrdinalIgnoreCase);

    private enum TokenKind
    {
        Term,
        And,
        Or,
        LParen,
        RParen,
        End,
    }

    private sealed record Token(TokenKind Kind, string Text, int Position);

    private sealed record CachedParse(string Query, FilterSearchParseResult Result);

    private sealed class ParseException(string message) : Exception(message);

    private sealed class Parser(List<Token> tokens)
    {
        private int _index;
        private int _depth;

        public bool IsAtEnd => Peek().Kind == TokenKind.End;

        public FilterSearchNode? ParseExpression()
        {
            var expr = ParseOrExpression();
            return expr;
        }

        private FilterSearchNode ParseOrExpression()
        {
            var left = ParseAndExpression();
            while (Match(TokenKind.Or))
            {
                var right = ParseAndExpression();
                left = new FilterSearchOrNode(left, right);
            }

            return left;
        }

        private FilterSearchNode ParseAndExpression()
        {
            var left = ParsePrimary();
            while (IsAndContinuation())
            {
                if (Match(TokenKind.And))
                {
                    // explicit and
                }

                var right = ParsePrimary();
                left = new FilterSearchAndNode(left, right);
            }

            return left;
        }

        private bool IsAndContinuation()
        {
            var kind = Peek().Kind;
            return kind is TokenKind.Term or TokenKind.LParen or TokenKind.And;
        }

        private FilterSearchNode ParsePrimary()
        {
            if (Match(TokenKind.LParen))
            {
                if (++_depth > MaxNestingDepth)
                {
                    throw new ParseException("Too many nested parentheses.");
                }

                var inner = ParseOrExpression();
                if (!Match(TokenKind.RParen))
                {
                    throw new ParseException("Missing ')'");
                }

                _depth--;
                return inner;
            }

            if (Peek().Kind == TokenKind.Term)
            {
                var token = Advance();
                return new FilterSearchTermNode(ParseTermToken(token.Text));
            }

            throw new ParseException($"Unexpected '{Peek().Text}'");
        }

        private bool Match(TokenKind kind)
        {
            if (Peek().Kind == kind)
            {
                Advance();
                return true;
            }

            return false;
        }

        private Token Peek() => tokens[_index];

        private Token Advance()
        {
            if (!IsAtEnd)
            {
                _index++;
            }

            return tokens[_index - 1];
        }
    }
}
