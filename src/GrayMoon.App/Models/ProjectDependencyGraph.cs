namespace GrayMoon.App.Models;

/// <summary>Graph for Cytoscape or similar: nodes (projects) and edges (dependencies). Edge source = dependent, target = referenced (dependent depends on referenced).</summary>
public sealed record ProjectDependencyGraph(
    IReadOnlyList<ProjectDependencyNode> Nodes,
    IReadOnlyList<ProjectDependencyEdge> Edges);

/// <summary>Node for dependency graph (e.g. Cytoscape element with id, label).</summary>
public sealed record ProjectDependencyNode(
    int ProjectId,
    string Label,
    string? PackageId,
    string ProjectName,
    string RepositoryName);

/// <summary>Edge for dependency graph: DependentProjectId -> ReferencedProjectId (dependent depends on referenced).</summary>
public sealed record ProjectDependencyEdge(
    int DependentProjectId,
    int ReferencedProjectId);
