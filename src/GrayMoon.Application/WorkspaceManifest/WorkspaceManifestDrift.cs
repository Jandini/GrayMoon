namespace GrayMoon.Application.WorkspaceManifest;

public sealed record WorkspaceManifestDrift(
    bool HasDrift,
    string? ParseError,
    IReadOnlyList<string> AddedRepositories,      // in file, not in database
    IReadOnlyList<string> RemovedRepositories,    // in database, not in file
    IReadOnlyList<string> ChangedProfileFields,
    IReadOnlyList<string> AddedConnectors,
    IReadOnlyList<string> RemovedConnectors)
{
    /// <summary>
    /// Names of repositories that are in the file and in the database with a different read-only state. The recorded
    /// <c>branch</c> and <c>commit</c> are not compared, like tag pins.
    /// </summary>
    public IReadOnlyList<string> ChangedReference { get; init; } = [];

    /// <summary>True when the read-only default for new repositories differs between the file and the database.</summary>
    public bool ReferenceDefaultChanged { get; init; }
}
