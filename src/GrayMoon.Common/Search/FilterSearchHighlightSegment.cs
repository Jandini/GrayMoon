namespace GrayMoon.Common.Search;

public enum FilterSearchHighlightKind
{
    Text,
    Whitespace,
    OperatorAnd,
    OperatorOr,
    Paren,
    FieldPrefix,
    FieldValue,
}

public sealed record FilterSearchExpressionHighlightSegment(
    string Text,
    FilterSearchHighlightKind Kind);
