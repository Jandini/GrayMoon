namespace GrayMoon.App.Models;

/// <summary>Row for repository build order grid. Same Sequence = can be built in parallel.</summary>
public sealed record RepositoryBuildOrderRow(
    int Sequence,
    string RepositoryName,
    int DependencyCount);
