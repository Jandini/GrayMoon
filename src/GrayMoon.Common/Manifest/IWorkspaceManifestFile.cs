namespace GrayMoon.Common.Manifest;

/// <summary>
/// The one place that knows the Workspace definition file (<c>graymoon.json</c>): its name, its format (schema v1, D15)
/// and how to read it from a folder on disk. The App reads the content through the Worker or a connector and the Worker
/// reads it from the working tree; both hand the text to this service, so every reader agrees on what the file says.
/// </summary>
public interface IWorkspaceManifestFile
{
    /// <summary>File name at the root of the Workspace repository (<see cref="WorkspaceManifestFile.FileName"/>).</summary>
    string FileName { get; }

    /// <summary>
    /// The newest schema version this GrayMoon reads. <see cref="Serialize"/> chooses the version it writes from the
    /// content: 2 when the definition holds <c>reference</c> or <c>branch</c>, otherwise 1.
    /// </summary>
    int CurrentSchemaVersion { get; }

    /// <summary>Byte-stable text: fixed property order, connectors and repositories sorted, LF line endings and a trailing newline.</summary>
    string Serialize(WorkspaceManifest manifest);

    /// <summary>
    /// Strict parse of the whole definition. False with <paramref name="error"/> for empty, invalid or newer-schema
    /// content, a missing <c>workspace</c> object, an invalid recorded revision, or <c>reference</c> / <c>branch</c> in a
    /// version-1 file. A leftover <c>workspace.name</c> is ignored.
    /// </summary>
    bool TryParse(string? content, out WorkspaceManifest? manifest, out string? error);

    /// <summary>True when the content is JSON whose <c>version</c> is newer than this GrayMoon understands.</summary>
    bool IsNewerSchema(string? content);

    /// <summary>
    /// The revision to check out for <paramref name="repositoryUrl"/>: the recorded commit, and the branch to create
    /// at it when the entry is a read-only repository on a branch (null for a tag pin). Commit null means that
    /// repository has no recorded revision. <paramref name="content"/> null means the definition file is absent
    /// (nothing to check out). An unreadable file or an invalid entry for that repository is an error, so the caller
    /// does not check out another revision. Entries for other repositories are not validated.
    /// </summary>
    (string? Commit, string? Branch, string? Error) ReadPinnedRevision(string? content, string? repositoryUrl);

    /// <summary>
    /// Folder names of the listed repositories, each an immediate child folder of the Workspace repository's working
    /// tree. Lenient: missing, unreadable or oddly shaped content lists nothing, never an error, because callers only
    /// use it to narrow work. Names that cannot be one folder name are skipped; duplicates (ignoring case) appear once.
    /// </summary>
    IReadOnlyList<string> ReadRepositoryFolderNames(string? content);

    /// <summary>Text of the definition at the root of <paramref name="directory"/>, or null when there is none. Throws when the file exists but cannot be read.</summary>
    Task<string?> ReadFromDirectoryAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary><see cref="ReadRepositoryFolderNames"/> for the definition at the root of <paramref name="directory"/>; empty when there is none or it cannot be read.</summary>
    IReadOnlyList<string> ReadRepositoryFolderNamesFromDirectory(string directory);
}
