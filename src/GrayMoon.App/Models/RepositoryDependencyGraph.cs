namespace GrayMoon.App.Models;

/// <summary>Repository-level dependency graph for Cytoscape. Edge: dependent repo → referenced repo.</summary>
public sealed record RepositoryDependencyGraph(
    IReadOnlyList<RepositoryDependencyNode> Nodes,
    IReadOnlyList<RepositoryDependencyEdge> Edges);

/// <summary>Node for repository dependency graph.</summary>
public sealed record RepositoryDependencyNode(int RepositoryId, string RepositoryName, ProjectType? RepositoryType = null);

/// <summary>Edge: DependentRepositoryId depends on ReferencedRepositoryId.</summary>
public sealed record RepositoryDependencyEdge(int DependentRepositoryId, int ReferencedRepositoryId);
