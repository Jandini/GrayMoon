namespace GrayMoon.Application.WorkspaceManifest;

public sealed record WorkspaceManifestDrift(
    bool HasDrift,
    string? ParseError,
    IReadOnlyList<string> AddedRepositories,      // in file, not in database
    IReadOnlyList<string> RemovedRepositories,    // in database, not in file
    IReadOnlyList<string> ChangedProfileFields,
    IReadOnlyList<string> AddedConnectors,
    IReadOnlyList<string> RemovedConnectors);
