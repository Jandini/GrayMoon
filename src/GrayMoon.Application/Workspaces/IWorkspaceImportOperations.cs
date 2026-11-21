using GrayMoon.Abstractions.Workspaces;

namespace GrayMoon.Application.Workspaces;

/// <summary>
/// Import from disk: a folder that already exists under the workspace root becomes a Workspace. Separate from Add
/// (which only creates a new, empty Workspace) and from Restore (which clones everything from a repository). Nothing
/// in the folder is deleted, reset or checked out; a failed import leaves the folder as it was.
/// </summary>
public interface IWorkspaceImportOperations
{
    /// <summary>Folders under the workspace root that are not a Workspace yet. Reads only.</summary>
    Task<ImportableFolderList> ListImportableFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only look at one folder: what kind it is and what an import would link. Changes nothing on disk or in
    /// the database.
    /// </summary>
    Task<ImportWorkspacePreflight> PreflightImportAsync(string folderName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports the folder as a Workspace named like the folder. Checks everything again first; a failure after the
    /// Workspace was created removes it again (the Git hooks it wrote, then its rows) and never touches the folder's
    /// content. <paramref name="options"/> is used only when the folder has no Workspace definition of its own.
    /// </summary>
    Task<ImportWorkspaceResult> ImportFromDiskAsync(
        string folderName,
        ImportWorkspaceOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <param name="Folders">Folder names, ordinal-ignore-case sorted.</param>
/// <param name="Error">Set when the Worker could not list the workspace root.</param>
public sealed record ImportableFolderList(IReadOnlyList<string> Folders, string? Error)
{
    public static ImportableFolderList Failed(string error) => new([], error);
}

/// <summary>What the folder is, which decides where the Workspace definition comes from.</summary>
public enum ImportFolderShape
{
    /// <summary>The folder is a Git repository with a valid Workspace definition file: the file decides everything.</summary>
    WorkspaceRepository,

    /// <summary>The folder is not a Git repository; it holds repositories. The user picks the profile.</summary>
    PlainFolder,

    /// <summary>
    /// The folder is a Git repository without a Workspace definition. Its inner repositories are imported; the
    /// folder's own repository is not linked and nothing is written into it.
    /// </summary>
    RepositoryWithoutDefinition,
}

/// <summary>The profile for a folder that has no Workspace definition.</summary>
public sealed record ImportWorkspaceOptions(
    WorkspaceType Type,
    WorkspaceVersioningMode VersioningMode,
    WorkspaceCiProvider CiProvider);

/// <summary>What an import of one folder would do, checked before anything is created.</summary>
public sealed record ImportWorkspacePreflight
{
    public bool Success { get; init; }

    /// <summary>Why the folder cannot be imported. Set only when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>True when the reason is a repository or connector that has to be imported through Connectors first.</summary>
    public bool NeedsConnectors { get; init; }

    public ImportFolderShape Shape { get; init; }

    /// <summary>The profile of the folder's definition; null when the user picks it (<see cref="ProfileFromDefinition"/> false).</summary>
    public WorkspaceType? WorkspaceType { get; init; }
    public WorkspaceVersioningMode? VersioningMode { get; init; }
    public WorkspaceCiProvider? CiProvider { get; init; }

    public bool ProfileFromDefinition => Shape == ImportFolderShape.WorkspaceRepository;

    /// <summary>Display name ("owner/name") of the Workspace repository; null unless the folder is one.</summary>
    public string? WorkspaceRepository { get; init; }

    /// <summary>Source repositories that are on disk and will be linked as they are.</summary>
    public IReadOnlyList<string> LinkedOnDisk { get; init; } = [];

    /// <summary>Source repositories the definition lists that are not on disk; the Sync after the import clones them.</summary>
    public IReadOnlyList<string> ToClone { get; init; } = [];

    /// <summary>Repositories on disk that will not be linked, each with the reason.</summary>
    public IReadOnlyList<string> NotLinked { get; init; } = [];

    /// <summary>Connectors the definition lists that are not configured on this computer.</summary>
    public IReadOnlyList<string> MissingConnectors { get; init; } = [];

    /// <summary>Repositories the definition lists that are not imported into GrayMoon ("owner/name").</summary>
    public IReadOnlyList<string> MissingRepositories { get; init; } = [];

    /// <summary>Listed Source repositories the definition marks read only.</summary>
    public int ReadOnlyCount { get; init; }

    /// <summary>Something the user should know that does not block the import.</summary>
    public string? Note { get; init; }

    public int RepositoryCount => LinkedOnDisk.Count + ToClone.Count;

    public static ImportWorkspacePreflight Failed(string error, bool needsConnectors = false) =>
        new() { Success = false, Error = error, NeedsConnectors = needsConnectors };
}

/// <summary>
/// Outcome of an import. <see cref="Success"/> means the Workspace exists; <see cref="Warnings"/> are things the user
/// should know after that. On failure nothing of the new Workspace is left in GrayMoon; <see cref="RollbackResidue"/>
/// says what could not be taken back. The folder's content is never changed by a failed import.
/// </summary>
public sealed record ImportWorkspaceResult
{
    public bool Success { get; init; }
    public int? WorkspaceId { get; init; }
    public string? Error { get; init; }
    public string? RollbackResidue { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> UnresolvedConnectors { get; init; } = [];
    public IReadOnlyList<string> UnresolvedRepositories { get; init; } = [];

    public static ImportWorkspaceResult Failed(string error, string? rollbackResidue = null) =>
        new() { Success = false, Error = error, RollbackResidue = rollbackResidue };
}
