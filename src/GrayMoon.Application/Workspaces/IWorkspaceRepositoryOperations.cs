using GrayMoon.Abstractions.Workspaces;

namespace GrayMoon.Application.Workspaces;

public interface IWorkspaceRepositoryOperations
{
    /// <summary>
    /// Links repositoryId as the Workspace repository and attaches it to the root (D4), then writes manifest and .gitignore (D5, D12).
    /// When the folder is already that repository, <c>graymoon.json</c> and <c>.gitignore</c> are left unchanged.
    /// <paramref name="setupBranchName"/> is a new branch (see <see cref="WorkspaceSetupBranch"/>) that is created from
    /// the checked-out default branch and checked out before the files are written; the files stay uncommitted and
    /// nothing is pushed. A name that already exists on the remote fails before anything changes. It is not used
    /// when the folder already is that repository or the repository has no commits. Null writes on the checked-out branch.
    /// </summary>
    Task<OperationResult> EnableWorkspaceRepositoryAsync(int workspaceId, int repositoryId, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default, string? setupBranchName = null);

    /// <summary>
    /// Removes a Workspace from GrayMoon. Needs a connected Worker: under the Workspace's structural lock the Worker
    /// lets go of its file watchers on the repository folders and removes the Git hooks GrayMoon installed there, and
    /// only then the Workspace row goes. Refused while the Workspace has Features. Nothing else on disk is touched.
    /// On failure nothing is deleted: <c>Error</c> says what was not done and <c>RepoErrors</c> names each repository
    /// and file that stopped it. <c>Warnings</c> after a success tell what was put back (the user's own hooks).
    /// </summary>
    Task<OperationResult> RemoveWorkspaceAsync(int workspaceId, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Names of the Workspace's Features, ordered. Remove Workspace is refused while this is not empty.</summary>
    Task<IReadOnlyList<string>> GetFeatureNamesAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves name, root, profile and the Source repository list of a Workspace. When the save removes a Source
    /// repository, its folder is detached first (Worker required, structural lock) exactly as in
    /// <see cref="RemoveWorkspaceAsync"/>, and a folder that cannot be cleaned stops the save. A save that removes
    /// nothing needs neither. A refusal (name taken, Features exist) is returned as a failure, not thrown.
    /// </summary>
    Task<OperationResult> SaveWorkspaceAsync(
        int workspaceId,
        string name,
        IReadOnlyCollection<int> repositoryIds,
        string? rootPath,
        WorkspaceType? type = null,
        WorkspaceVersioningMode? versioningMode = null,
        WorkspaceCiProvider? ciProvider = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the Workspace-role link. The Worker first removes GrayMoon's Git hooks from the Workspace folder and
    /// lets go of its watcher there (Worker required, structural lock). Never deletes files or .git.
    /// </summary>
    Task<OperationResult> DisableWorkspaceRepositoryAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only check of an imported repository before a restore: reads <c>graymoon.json</c> through the connector
    /// (no Worker, no clone, no database change), validates it and previews what a restore would resolve locally.
    /// <paramref name="branchName"/> is the remote branch to read it from; null reads the default branch.
    /// </summary>
    Task<RestoreWorkspacePreflight> PreflightRestoreAsync(int repositoryId, CancellationToken cancellationToken = default, string? branchName = null);

    /// <summary>
    /// Restores a Workspace from a repository that holds a valid Workspace definition. Validates everything it can
    /// before creating anything; a failure after the Workspace was created rolls it back (database row, links and
    /// the root folder when the Worker can prove the restore created it). <paramref name="branchName"/> is the branch the
    /// Workspace repository is checked out on (and the one whose definition is validated); null restores the default branch.
    /// </summary>
    Task<RestoreWorkspaceResult> RestoreFromRepositoryAsync(int repositoryId, string workspaceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default, string? branchName = null);
}

/// <summary>
/// Outcome of a restore. <see cref="Success"/> means the Workspace exists and its definition was applied;
/// <see cref="Warning"/> is a non-fatal problem after that (for example the managed .gitignore could not be written).
/// On failure nothing of the new Workspace is left in GrayMoon; <see cref="CleanupResidue"/> says what could not be
/// removed from disk.
/// </summary>
public sealed record RestoreWorkspaceResult
{
    public bool Success { get; init; }
    public int? WorkspaceId { get; init; }
    public string? Error { get; init; }
    public string? Warning { get; init; }
    public string? CleanupResidue { get; init; }

    /// <summary>Connectors named by the definition that are not configured on this computer (display text).</summary>
    public IReadOnlyList<string> UnresolvedConnectors { get; init; } = [];

    /// <summary>Repositories named by the definition that are not imported into GrayMoon (display text, "owner/name").</summary>
    public IReadOnlyList<string> UnresolvedRepositories { get; init; } = [];

    public static RestoreWorkspaceResult Failed(string error, string? cleanupResidue = null) =>
        new() { Success = false, Error = error, CleanupResidue = cleanupResidue };
}

/// <summary>What a restore of one repository would do, checked before anything is created.</summary>
public sealed record RestoreWorkspacePreflight
{
    public bool Success { get; init; }

    /// <summary>Why the repository cannot be restored. Set only when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }

    public WorkspaceType? WorkspaceType { get; init; }
    public WorkspaceVersioningMode? VersioningMode { get; init; }
    public WorkspaceCiProvider? CiProvider { get; init; }

    /// <summary>Source repositories the definition lists (the Workspace repository itself is not counted).</summary>
    public int RepositoryCount { get; init; }

    /// <summary>Connectors the definition lists.</summary>
    public int ConnectorCount { get; init; }

    public IReadOnlyList<string> MissingConnectors { get; init; } = [];
    public IReadOnlyList<string> MissingRepositories { get; init; } = [];

    public int ConfiguredConnectorCount => ConnectorCount - MissingConnectors.Count;

    /// <summary>None / mixed / all of the listed Source repositories that the definition marks read-only.</summary>
    public WorkspaceReferenceAggregate ReferenceAggregate { get; init; }

    /// <summary>True when the definition makes new repositories read-only.</summary>
    public bool ReferenceDefault { get; init; }

    public static RestoreWorkspacePreflight Failed(string error) => new() { Success = false, Error = error };
}

/// <summary>Remote branches of an imported repository, read through its connector (no Worker, no clone).</summary>
/// <param name="DefaultBranchName">The provider's default branch; null when it names none.</param>
/// <param name="Branches">Branch names, default first, then ordinal-ignore-case sorted. Never silently shortened.</param>
/// <param name="Truncated">True when the provider has more branches than were read.</param>
/// <param name="Error">Set when the list could not be read at all.</param>
public sealed record RemoteBranchList(string? DefaultBranchName, IReadOnlyList<string> Branches, bool Truncated, string? Error)
{
    public static RemoteBranchList Failed(string error) => new(null, Array.Empty<string>(), false, error);
}

public interface IRemoteRepositoryBranchReader
{
    Task<RemoteBranchList> ListAsync(int repositoryId, CancellationToken cancellationToken = default);
}
