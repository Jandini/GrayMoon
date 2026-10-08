using GrayMoon.Abstractions.Workspaces;

namespace GrayMoon.Application.Workspaces;

public interface IWorkspaceRepositoryOperations
{
    /// <summary>Links repositoryId as the Workspace repository and attaches it to the root (D4), then writes manifest and .gitignore (D5, D12).</summary>
    Task<OperationResult> EnableWorkspaceRepositoryAsync(int workspaceId, int repositoryId, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Removes the Workspace-role link only. Never deletes files or .git.</summary>
    Task<OperationResult> DisableWorkspaceRepositoryAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only check of an imported repository before a restore: reads <c>.graymoon.json</c> through the connector
    /// (no Worker, no clone, no database change), validates it and previews what a restore would resolve locally.
    /// </summary>
    Task<RestoreWorkspacePreflight> PreflightRestoreAsync(int repositoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a Workspace from a repository that holds a valid Workspace definition. Validates everything it can
    /// before creating anything; a failure after the Workspace was created rolls it back (database row, links and
    /// the root folder when the Worker can prove the restore created it).
    /// </summary>
    Task<RestoreWorkspaceResult> RestoreFromRepositoryAsync(int repositoryId, string workspaceName, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default);
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

    /// <summary>The Workspace name stored in the definition.</summary>
    public string? DefinitionName { get; init; }

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

    public static RestoreWorkspacePreflight Failed(string error) => new() { Success = false, Error = error };
}
