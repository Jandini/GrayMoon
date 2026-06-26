namespace GrayMoon.App.Models;

/// <summary>Implicit (non-custom) referenced repository IDs for a dependent repo, split by source.</summary>
public sealed record ImplicitReferencedRepoIdsBySource(
    HashSet<int> FromProject,
    HashSet<int> FromFile);
