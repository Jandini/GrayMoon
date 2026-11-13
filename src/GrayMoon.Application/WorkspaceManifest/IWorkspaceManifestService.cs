namespace GrayMoon.Application.WorkspaceManifest;

// Inside the namespace, so WorkspaceManifest binds to the type and not to this namespace.
using GrayMoon.Common.Manifest;

/// <summary>
/// Keeps the Workspace definition of a Workspace in step with its database: builds it, writes it, detects drift. The
/// file's name, format and parsing belong to <see cref="IWorkspaceManifestFile"/>.
/// </summary>
public interface IWorkspaceManifestService
{
    Task<WorkspaceManifest> BuildFromDatabaseAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<OperationResult> WriteAuthoritativeManifestAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates tag pins on existing repository entries in <c>graymoon.json</c> and writes the file when it changed.
    /// Does not create a git commit. Does nothing when the Workspace has no Workspace repository.
    /// </summary>
    Task<OperationResult> SetRepositoryTagPinsAsync(int workspaceId, IReadOnlyList<WorkspaceRepositoryTagPinChange> changes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the branch and commit of read-only repositories on existing entries in <c>graymoon.json</c> and writes
    /// the file when it changed. Does not create a git commit. Does nothing when the Workspace has no Workspace
    /// repository. Returns a failed result instead of throwing; an entry that is not read-only in the file is a failure.
    /// </summary>
    Task<OperationResult> SetRepositoryReferenceRevisionsAsync(int workspaceId, IReadOnlyList<WorkspaceRepositoryReferenceRevisionChange> changes, CancellationToken cancellationToken = default);
    /// <summary>
    /// Replaces <c>versionFiles</c> in <c>graymoon.json</c> with the version configurations in the database and writes
    /// the file when it changed. Nothing else in the file changes. Does not create a git commit. Does nothing when the
    /// Workspace has no Workspace repository; fails when the definition is missing or cannot be read.
    /// </summary>
    Task<OperationResult> WriteVersionFilesAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges version files of a Workspace definition into the database (Restore): the definition is the source of
    /// truth, so its pattern replaces an existing configuration of the same file, and a missing Workspace file is
    /// added. Configurations the definition does not list are kept. Returns the entries whose repository is not
    /// linked to the Workspace (<c>repository/path</c>); those are skipped.
    /// </summary>
    Task<IReadOnlyList<string>> MergeVersionFilesIntoDatabaseAsync(int workspaceId, IReadOnlyList<WorkspaceManifestVersionFile>? versionFiles, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads <c>versionFiles</c> from the <c>graymoon.json</c> in the root of the Workspace repository of the special
    /// Workspace and merges them into the database with <see cref="MergeVersionFilesIntoDatabaseAsync"/>. No other
    /// repository is searched. Does nothing (success) when the Workspace has no Workspace repository, the file is
    /// absent, or it lists no version files; fails when the file cannot be read or parsed.
    /// </summary>
    Task<OperationResult> MergeVersionFilesFromDefinitionAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<OperationResult> WriteManagedGitIgnoreAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the Workspace definition (<c>graymoon.json</c> of the special Workspace) has <c>"codegraph": true</c>.
    /// False when the property is false or absent, when there is no Workspace repository, or when it cannot be read.
    /// Never throws.
    /// </summary>
    Task<bool> IsCodeGraphEnabledAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The workspace-wide CodeGraph switch from <c>graymoon.json</c>. <c>true</c> or <c>false</c> when the definition
    /// sets <c>codegraph</c>. Null when the property is absent, there is no Workspace repository, no definition, or it
    /// cannot be read. Never throws. An explicit false turns the integration off; null does not.
    /// </summary>
    Task<bool?> GetCodeGraphSwitchAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <c>"codegraph": true</c> or <c>false</c> into <c>graymoon.json</c> and leaves every other property alone.
    /// Fails when the Workspace has no Workspace repository or the definition cannot be read. Does not delete an index.
    /// </summary>
    Task<OperationResult> SetCodeGraphEnabledAsync(int workspaceId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// The <c>ai</c> section of the Workspace definition (<c>graymoon.json</c> of the special Workspace): per-provider
    /// model choices. Null with no error when there is no Workspace repository, no definition or no <c>ai</c> section;
    /// an error when the definition exists but cannot be read or parsed, so the caller never silently ignores a broken file.
    /// </summary>
    Task<(WorkspaceManifestAi? Ai, string? Error)> ReadAiAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <c>codegraph.json</c> (its <c>include</c> lists every source repository folder) into the Workspace
    /// repository of <paramref name="contextId"/>, or of the special Workspace when null. Does not check the
    /// <c>codegraph</c> setting; does nothing when the Workspace has no Workspace repository.
    /// </summary>
    Task<OperationResult> WriteCodeGraphConfigAsync(int workspaceId, Features.WorkspaceFeatureContextId? contextId = null, CancellationToken cancellationToken = default);
    Task<WorkspaceManifestDrift> DetectDriftAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same service, reading and writing the Workspace repository checkout of <paramref name="contextId"/>
    /// (a Feature's worktree) wherever this service otherwise uses the special Workspace. Workspace settings opened
    /// from a context use it, so <c>graymoon.json</c>, <c>.gitignore</c> and <c>codegraph.json</c> change in the
    /// folder the user is working in and the special Workspace's files are left alone. The database is not
    /// per-context. A test double that keeps no files returns itself.
    /// </summary>
    IWorkspaceManifestService ForContext(Features.WorkspaceFeatureContextId contextId) => this;
}
