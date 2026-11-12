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

    /// <summary>Most repository names one sentence lists; the rest is "and N more".</summary>
    public const int MaxNamedRepositories = 5;

    /// <summary>The read-only differences as text for the drift banner; null when there is none.</summary>
    public string? DescribeReferenceDifferences()
    {
        var parts = new List<string>(2);
        if (ChangedReference.Count > 0)
        {
            var names = string.Join(", ", ChangedReference.Take(MaxNamedRepositories));
            var more = ChangedReference.Count - MaxNamedRepositories;
            parts.Add(more > 0
                ? $"The read only state differs for {names} and {more} more."
                : $"The read only state differs for {names}.");
        }

        if (ReferenceDefaultChanged)
            parts.Add("The read only default for new repositories differs.");

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
