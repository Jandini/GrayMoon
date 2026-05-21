namespace GrayMoon.Common.Search;

/// <summary>
/// Parses and evaluates boolean repository search queries:
/// implicit AND between terms, explicit <c>and</c>/<c>or</c>, parentheses,
/// and <c>field:value</c> leaves (e.g. <c>topic:blazor</c>).
/// </summary>
public static class RepositorySearch
{
    public static bool IsValidQuery(string? query) =>
        string.IsNullOrWhiteSpace(query) || Parse(query).IsValid;

    public static RepositorySearchParseResult Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return RepositorySearchParseResult.Success(null);
        }

        try
        {
            var tokens = Tokenize(query);
            var parser = new Parser(tokens);
            var expression = parser.ParseExpression();
            if (!parser.IsAtEnd)
            {
                return RepositorySearchParseResult.Failure("Unexpected text after expression.");
            }

            return RepositorySearchParseResult.Success(expression);
        }
        catch (ParseException ex)
        {
            return RepositorySearchParseResult.Failure(ex.Message);
        }
    }

    public static bool Evaluate(RepositorySearchNode node, Func<RepositorySearchTerm, bool> matchTerm) =>
        node switch
        {
            RepositorySearchTermNode t => matchTerm(t.Term),
            RepositorySearchAndNode a => Evaluate(a.Left, matchTerm) && Evaluate(a.Right, matchTerm),
            RepositorySearchOrNode o => Evaluate(o.Left, matchTerm) || Evaluate(o.Right, matchTerm),
            _ => false,
        };

    /// <summary>
    /// Returns whether the query matches using the expression parser, or legacy space-AND fallback when invalid.
    /// </summary>
    public static bool Matches(string? query, Func<RepositorySearchTerm, bool> matchTerm)
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

    public static IReadOnlyList<RepositorySearchHighlightSegment> GetHighlightSegments(string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return Array.Empty<RepositorySearchHighlightSegment>();
        }

        return LexForHighlight(query);
    }

    private static bool LegacyAllMatch(string? query, Func<RepositorySearchTerm, bool> matchTerm)
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

    private static RepositorySearchTerm ParseTermToken(string token)
    {
        var colon = token.IndexOf(':');
        if (colon > 0)
        {
            var field = token[..colon];
            var value = token[(colon + 1)..];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return new RepositorySearchTerm(field.ToLowerInvariant(), value);
            }
        }

        return new RepositorySearchTerm(null, token);
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

    private static IReadOnlyList<RepositorySearchHighlightSegment> LexForHighlight(string query)
    {
        var segments = new List<RepositorySearchHighlightSegment>();
        var i = 0;
        while (i < query.Length)
        {
            var c = query[i];
            if (char.IsWhiteSpace(c))
            {
                var wsStart = i;
                while (i < query.Length && char.IsWhiteSpace(query[i]))
                {
                    i++;
                }

                segments.Add(new RepositorySearchHighlightSegment(
                    query[wsStart..i],
                    RepositorySearchHighlightKind.Whitespace));
                continue;
            }

            if (c is '(' or ')')
            {
                segments.Add(new RepositorySearchHighlightSegment(
                    c.ToString(),
                    RepositorySearchHighlightKind.Paren));
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
                segments.Add(new RepositorySearchHighlightSegment(text, RepositorySearchHighlightKind.OperatorAnd));
            }
            else if (IsOperator(text, "or"))
            {
                segments.Add(new RepositorySearchHighlightSegment(text, RepositorySearchHighlightKind.OperatorOr));
            }
            else
            {
                AddTermHighlightSegments(segments, text);
            }
        }

        return segments;
    }

    private static void AddTermHighlightSegments(List<RepositorySearchHighlightSegment> segments, string text)
    {
        var colon = text.IndexOf(':');
        if (colon > 0)
        {
            var value = text[(colon + 1)..];
            if (!string.IsNullOrWhiteSpace(value))
            {
                segments.Add(new RepositorySearchHighlightSegment(
                    text[..(colon + 1)],
                    RepositorySearchHighlightKind.FieldPrefix));
                segments.Add(new RepositorySearchHighlightSegment(
                    value,
                    RepositorySearchHighlightKind.FieldValue));
                return;
            }
        }

        segments.Add(new RepositorySearchHighlightSegment(text, RepositorySearchHighlightKind.Text));
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

    private sealed class ParseException(string message) : Exception(message);

    private sealed class Parser(List<Token> tokens)
    {
        private int _index;

        public bool IsAtEnd => Peek().Kind == TokenKind.End;

        public RepositorySearchNode? ParseExpression()
        {
            var expr = ParseOrExpression();
            return expr;
        }

        private RepositorySearchNode ParseOrExpression()
        {
            var left = ParseAndExpression();
            while (Match(TokenKind.Or))
            {
                var right = ParseAndExpression();
                left = new RepositorySearchOrNode(left, right);
            }

            return left;
        }

        private RepositorySearchNode ParseAndExpression()
        {
            var left = ParsePrimary();
            while (IsAndContinuation())
            {
                if (Match(TokenKind.And))
                {
                    // explicit and
                }

                var right = ParsePrimary();
                left = new RepositorySearchAndNode(left, right);
            }

            return left;
        }

        private bool IsAndContinuation()
        {
            var kind = Peek().Kind;
            return kind is TokenKind.Term or TokenKind.LParen or TokenKind.And;
        }

        private RepositorySearchNode ParsePrimary()
        {
            if (Match(TokenKind.LParen))
            {
                var inner = ParseOrExpression();
                if (!Match(TokenKind.RParen))
                {
                    throw new ParseException("Missing ')'");
                }

                return inner;
            }

            if (Peek().Kind == TokenKind.Term)
            {
                var token = Advance();
                return new RepositorySearchTermNode(ParseTermToken(token.Text));
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
