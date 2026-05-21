namespace GrayMoon.Common.Search;

public abstract record FilterSearchNode;

public sealed record FilterSearchTermNode(FilterSearchTerm Term) : FilterSearchNode;

public sealed record FilterSearchAndNode(FilterSearchNode Left, FilterSearchNode Right) : FilterSearchNode;

public sealed record FilterSearchOrNode(FilterSearchNode Left, FilterSearchNode Right) : FilterSearchNode;
