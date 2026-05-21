namespace GrayMoon.Common.Search;

public abstract record RepositorySearchNode;

public sealed record RepositorySearchTermNode(RepositorySearchTerm Term) : RepositorySearchNode;

public sealed record RepositorySearchAndNode(RepositorySearchNode Left, RepositorySearchNode Right) : RepositorySearchNode;

public sealed record RepositorySearchOrNode(RepositorySearchNode Left, RepositorySearchNode Right) : RepositorySearchNode;
