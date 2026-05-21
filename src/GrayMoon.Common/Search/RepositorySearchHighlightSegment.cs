namespace GrayMoon.Common.Search;

public enum RepositorySearchHighlightKind
{
    Text,
    Whitespace,
    OperatorAnd,
    OperatorOr,
    Paren,
    FieldPrefix,
    FieldValue,
}

public sealed record RepositorySearchHighlightSegment(
    string Text,
    RepositorySearchHighlightKind Kind);
