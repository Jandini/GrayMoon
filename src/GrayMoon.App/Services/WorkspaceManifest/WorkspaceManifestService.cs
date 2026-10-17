using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services.GitHub;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.WorkspaceManifest;

// Inside the namespace, so WorkspaceManifest binds to the type and not to this namespace.
using GrayMoon.Common.Manifest;
using IWorkspaceManifestService = GrayMoon.Application.WorkspaceManifest.IWorkspaceManifestService;
using WorkspaceManifestDrift = GrayMoon.Application.WorkspaceManifest.WorkspaceManifestDrift;
using WorkspaceRepositoryTagPinChange = GrayMoon.Application.WorkspaceManifest.WorkspaceRepositoryTagPinChange;

/// <summary>
/// Builds, writes and compares the Workspace definition (<c>graymoon.json</c>) and the managed <c>.gitignore</c>
/// section of the Workspace repository (D5, D8, D11, D12). The file's name and format come from
/// <see cref="IWorkspaceManifestFile"/>. Uses factory-created contexts, so it is safe to run outside the circuit's DbContext.
/// </summary>
public sealed class WorkspaceManifestService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IWorkspaceManifestFile manifestFile,
    IWorkerBridge workerBridge,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceFeatureContextResolver contextResolver,
    ILogger<WorkspaceManifestService> logger) : IWorkspaceManifestService
{
    internal const string NoWorkspaceRepositoryMessage = "No Workspace repository";

    public async Task<WorkspaceManifest> BuildFromDatabaseAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var workspace = await db.Workspaces
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, cancellationToken)
            ?? throw new InvalidOperationException($"Workspace {workspaceId} not found.");

        var sourceLinks = await db.WorkspaceRepositories
            .AsNoTracking()
            .Include(l => l.Repository)
            .ThenInclude(r => r!.Connector)
            .Where(l => l.WorkspaceId == workspaceId && l.Role == WorkspaceRepositoryRole.Source)
            .ToListAsync(cancellationToken);
        var sourceRepositories = sourceLinks
            .Where(l => l.Repository is not null)
            .Select(l => l.Repository!)
            .ToList();

        var connectors = sourceRepositories
            .Select(r => r.Connector)
            .Where(c => c is { ConnectorType: ConnectorType.GitHub })
            .Select(c => ConnectorUrl(c))
            .Where(url => url.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(url => url, StringComparer.OrdinalIgnoreCase)
            .Select(url => new WorkspaceManifestConnector("github", url))
            .ToList();

        var repositories = sourceRepositories
            .Select(r => new WorkspaceManifestRepository(r.RepositoryName, r.CloneUrl, ConnectorUrl(r.Connector)))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var versionFiles = await BuildVersionFilesAsync(db, workspaceId, cancellationToken);

        return new WorkspaceManifest(
            manifestFile.CurrentSchemaVersion,
            new WorkspaceManifestWorkspace(
                workspace.Name,
                new WorkspaceManifestProfile(
                    WorkspaceManifestProfileNames.ToManifest(workspace.Type),
                    WorkspaceManifestProfileNames.ToManifest(workspace.VersioningMode),
                    WorkspaceManifestProfileNames.ToManifest(workspace.CiProvider))),
            connectors,
            repositories,
            VersionFiles: versionFiles.Count > 0 ? versionFiles : null);
    }

    /// <summary>
    /// Every configured version file of a repository still linked to the Workspace (Source or Workspace role). A
    /// configuration whose pattern has no lines is left out.
    /// </summary>
    private static async Task<List<WorkspaceManifestVersionFile>> BuildVersionFilesAsync(
        AppDbContext db,
        int workspaceId,
        CancellationToken cancellationToken)
    {
        var linkedRepositoryIds = db.WorkspaceRepositories
            .Where(l => l.WorkspaceId == workspaceId)
            .Select(l => l.RepositoryId);

        var rows = await db.WorkspaceFileVersionConfigs
            .AsNoTracking()
            .Where(c => c.File!.WorkspaceId == workspaceId && linkedRepositoryIds.Contains(c.File.RepositoryId))
            .Select(c => new { c.File!.Repository!.RepositoryName, c.File.FilePath, c.VersionPattern })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new WorkspaceManifestVersionFile(
                r.RepositoryName,
                WorkspaceManifestVersionFile.NormalizePath(r.FilePath),
                WorkspaceManifestVersionFile.SplitPattern(r.VersionPattern)))
            .Where(f => f.Path.Length > 0 && f.Pattern.Count > 0)
            .OrderBy(f => f.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<OperationResult> WriteVersionFilesAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var (_, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);
            if (args.WorkspaceRepositoryName is null)
                return new OperationResult(true, NoWorkspaceRepositoryMessage);

            var read = await WorkspaceRepositoryFileAccess.ReadAsync(
                workerBridge, args, manifestFile.FileName, cancellationToken);
            if (read.Error is not null)
                return OperationResult.Fail(read.Error);
            if (!read.Found || string.IsNullOrWhiteSpace(read.Content))
                return OperationResult.Fail("The Workspace definition was not found.");
            if (!manifestFile.TryParse(read.Content, out var manifest, out var parseError) || manifest is null)
                return OperationResult.Fail(parseError ?? "The Workspace definition could not be read.");

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var versionFiles = await WithUnlinkedEntriesAsync(
                db, workspaceId, await BuildVersionFilesAsync(db, workspaceId, cancellationToken), manifest.VersionFiles, cancellationToken);
            var content = manifestFile.Serialize(manifest with { VersionFiles = versionFiles.Count > 0 ? versionFiles : null });
            if (string.Equals(content, read.Content.Replace("\r\n", "\n"), StringComparison.Ordinal))
                return OperationResult.Ok();

            // The file is updated in the working tree only. The user commits the Workspace repository.
            return await WorkspaceRepositoryFileAccess.WriteAsync(
                workerBridge, args, manifestFile.FileName, content, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Writing version files to the Workspace definition failed. WorkspaceId={WorkspaceId}", workspaceId);
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> MergeVersionFilesIntoDatabaseAsync(
        int workspaceId,
        IReadOnlyList<WorkspaceManifestVersionFile>? versionFiles,
        CancellationToken cancellationToken = default)
    {
        var skipped = new List<string>();
        if (versionFiles is not { Count: > 0 })
            return skipped;

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var repositoryIdsByName = (await db.WorkspaceRepositories
                .AsNoTracking()
                .Where(l => l.WorkspaceId == workspaceId && l.Repository != null)
                .Select(l => new { l.RepositoryId, l.Repository!.RepositoryName })
                .ToListAsync(cancellationToken))
            .GroupBy(l => l.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().RepositoryId, StringComparer.OrdinalIgnoreCase);

        var files = await db.WorkspaceFiles
            .Where(f => f.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        var configs = await db.WorkspaceFileVersionConfigs
            .Where(c => c.File!.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        foreach (var entry in versionFiles)
        {
            var path = WorkspaceManifestVersionFile.NormalizePath(entry.Path);
            if (entry.Pattern.Count == 0 || path.Length == 0)
                continue;

            if (!repositoryIdsByName.TryGetValue(entry.Repository, out var repositoryId))
            {
                skipped.Add($"{entry.Repository}/{path}");
                continue;
            }

            var file = files.FirstOrDefault(f =>
                f.RepositoryId == repositoryId
                && string.Equals(WorkspaceManifestVersionFile.NormalizePath(f.FilePath), path, StringComparison.OrdinalIgnoreCase));
            if (file is null)
            {
                file = new WorkspaceFile
                {
                    WorkspaceId = workspaceId,
                    RepositoryId = repositoryId,
                    FileName = Path.GetFileName(path),
                    FilePath = path,
                };
                db.WorkspaceFiles.Add(file);
                files.Add(file);
            }

            // The definition is the source of truth: its pattern replaces the one in the database.
            var pattern = entry.PatternText();
            // Tracked configs have their File fixed up by EF, so this also finds one added earlier in this loop.
            var config = configs.FirstOrDefault(c => ReferenceEquals(c.File, file));
            if (config is null)
            {
                config = new WorkspaceFileVersionConfig { File = file, VersionPattern = pattern };
                db.WorkspaceFileVersionConfigs.Add(config);
                configs.Add(config);
            }
            else
            {
                config.VersionPattern = pattern;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (skipped.Count > 0)
        {
            logger.LogInformation(
                "Version files of repositories not in the Workspace were not imported. WorkspaceId={WorkspaceId} Files={Files}",
                workspaceId, string.Join(", ", skipped));
        }

        return skipped;
    }

    public async Task<OperationResult> MergeVersionFilesFromDefinitionAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var (_, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);
            if (args.WorkspaceRepositoryName is null)
                return new OperationResult(true, NoWorkspaceRepositoryMessage);

            // Only the Workspace repository is read; a source repository's own graymoon.json is never looked at.
            var read = await WorkspaceRepositoryFileAccess.ReadAsync(
                workerBridge, args, manifestFile.FileName, cancellationToken);
            if (read.Error is not null)
                return OperationResult.Fail(read.Error);
            if (!read.Found || string.IsNullOrWhiteSpace(read.Content))
                return OperationResult.Ok();
            if (!manifestFile.TryParse(read.Content, out var manifest, out var parseError) || manifest is null)
                return OperationResult.Fail(parseError ?? "The Workspace definition could not be read.");

            await MergeVersionFilesIntoDatabaseAsync(workspaceId, manifest.VersionFiles, cancellationToken);
            return OperationResult.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Importing version files from the Workspace definition failed. WorkspaceId={WorkspaceId}", workspaceId);
            return OperationResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// The version files to write: those of the database, plus the entries already in the file whose repository is not
    /// linked to the Workspace. Those could not be imported (see <see cref="MergeVersionFilesIntoDatabaseAsync"/>), so
    /// dropping them would lose them. An entry of a linked repository is not kept: the database is authoritative for it,
    /// and Sync imports file-only entries before anything is written.
    /// </summary>
    private static async Task<List<WorkspaceManifestVersionFile>> WithUnlinkedEntriesAsync(
        AppDbContext db,
        int workspaceId,
        List<WorkspaceManifestVersionFile> databaseFiles,
        IReadOnlyList<WorkspaceManifestVersionFile>? fileEntries,
        CancellationToken cancellationToken)
    {
        if (fileEntries is not { Count: > 0 })
            return databaseFiles;

        var linkedNames = (await db.WorkspaceRepositories
                .AsNoTracking()
                .Where(l => l.WorkspaceId == workspaceId && l.Repository != null)
                .Select(l => l.Repository!.RepositoryName)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = databaseFiles.ToList();
        foreach (var entry in fileEntries)
        {
            var path = WorkspaceManifestVersionFile.NormalizePath(entry.Path);
            if (path.Length == 0 || entry.Pattern.Count == 0 || linkedNames.Contains(entry.Repository))
                continue;
            if (result.Any(f => string.Equals(f.Repository, entry.Repository, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(entry with { Path = path });
        }

        return result
            .OrderBy(f => f.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<OperationResult> WriteAuthoritativeManifestAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var (_, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);
            if (args.WorkspaceRepositoryName is null)
                return new OperationResult(true, NoWorkspaceRepositoryMessage);

            var manifest = await BuildFromDatabaseAsync(workspaceId, cancellationToken);
            var read = await WorkspaceRepositoryFileAccess.ReadAsync(
                workerBridge, args, manifestFile.FileName, cancellationToken);
            if (read.Error is not null)
                return OperationResult.Fail($"Could not read the Workspace definition: {read.Error}");
            if (read.Found
                && !string.IsNullOrWhiteSpace(read.Content)
                && manifestFile.TryParse(read.Content, out var existing, out _)
                && existing is not null)
            {
                manifest = WorkspaceRepositoryTagPins.CopyOnto(manifest, existing);
                // File-owned sections: the database has no copy of them, so the file's value is kept.
                manifest = manifest with { CodeGraph = existing.CodeGraph, Ai = existing.Ai };

                // Version files of repositories that are not linked here could not be imported; keep them in the file.
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var versionFiles = await WithUnlinkedEntriesAsync(
                    db, workspaceId, manifest.VersionFiles?.ToList() ?? [], existing.VersionFiles, cancellationToken);
                manifest = manifest with { VersionFiles = versionFiles.Count > 0 ? versionFiles : null };
            }

            var content = manifestFile.Serialize(manifest);
            return await WorkspaceRepositoryFileAccess.WriteAsync(
                workerBridge, args, manifestFile.FileName, content, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Writing the Workspace definition failed. WorkspaceId={WorkspaceId}", workspaceId);
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<OperationResult> SetRepositoryTagPinsAsync(
        int workspaceId,
        IReadOnlyList<WorkspaceRepositoryTagPinChange> changes,
        CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0)
            return OperationResult.Ok();

        try
        {
            var (_, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);
            if (args.WorkspaceRepositoryName is null)
                return new OperationResult(true, NoWorkspaceRepositoryMessage);

            var read = await WorkspaceRepositoryFileAccess.ReadAsync(
                workerBridge, args, manifestFile.FileName, cancellationToken);
            if (read.Error is not null)
                return OperationResult.Fail(read.Error);
            if (!read.Found || string.IsNullOrWhiteSpace(read.Content))
                return OperationResult.Fail("The Workspace definition was not found.");
            if (!manifestFile.TryParse(read.Content, out var manifest, out var parseError) || manifest is null)
                return OperationResult.Fail(parseError ?? "The Workspace definition could not be read.");
            if (!WorkspaceRepositoryTagPins.TryApply(manifest, changes, out var updated, out var applyError))
                return OperationResult.Fail(applyError ?? "The repository tag could not be recorded.");

            var content = manifestFile.Serialize(updated);
            if (string.Equals(content, read.Content.Replace("\r\n", "\n"), StringComparison.Ordinal))
                return OperationResult.Ok();

            // The file is updated in the working tree only. The user commits the Workspace repository.
            return await WorkspaceRepositoryFileAccess.WriteAsync(
                workerBridge, args, manifestFile.FileName, content, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Recording repository tag pins failed. WorkspaceId={WorkspaceId}", workspaceId);
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<OperationResult> WriteManagedGitIgnoreAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var (_, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);
            if (args.WorkspaceRepositoryName is null)
                return new OperationResult(true, NoWorkspaceRepositoryMessage);

            var read = await WorkspaceRepositoryFileAccess.ReadAsync(
                workerBridge, args, WorkspaceRepositoryFileAccess.GitIgnoreFilePath, cancellationToken);
            if (read.Error is not null)
                return OperationResult.Fail(read.Error);

            var manifest = await BuildFromDatabaseAsync(workspaceId, cancellationToken);
            // CodeGraph's data folder is never committed: otherwise `codegraph uninit` (which deletes it) leaves
            // tracked files deleted and git refuses to remove the Feature worktree.
            var content = ManagedGitIgnoreSection.Apply(
                read.Content,
                manifest.Repositories.Select(r => r.Name),
                await IsCodeGraphEnabledAsync(workspaceId, cancellationToken));
            return await WorkspaceRepositoryFileAccess.WriteAsync(
                workerBridge, args, WorkspaceRepositoryFileAccess.GitIgnoreFilePath, content, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Writing the managed .gitignore section failed. WorkspaceId={WorkspaceId}", workspaceId);
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<bool> IsCodeGraphEnabledAsync(int workspaceId, CancellationToken cancellationToken = default)
        => await GetCodeGraphSwitchAsync(workspaceId, cancellationToken) == true;

    public async Task<bool?> GetCodeGraphSwitchAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var (_, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);
            if (args.WorkspaceRepositoryName is null)
                return null;

            var read = await WorkspaceRepositoryFileAccess.ReadAsync(
                workerBridge, args, manifestFile.FileName, cancellationToken);
            if (!read.Found || !manifestFile.TryParse(read.Content ?? string.Empty, out var manifest, out _))
                return null;

            return manifest?.CodeGraph;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reading the codegraph setting failed. WorkspaceId={WorkspaceId}", workspaceId);
            return null;
        }
    }

    public async Task<(WorkspaceManifestAi? Ai, string? Error)> ReadAiAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var (_, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);
        if (args.WorkspaceRepositoryName is null)
            return (null, null);

        var read = await WorkspaceRepositoryFileAccess.ReadAsync(
            workerBridge, args, manifestFile.FileName, cancellationToken);
        if (read.Error is not null)
            return (null, $"Could not read {manifestFile.FileName}: {read.Error}");
        if (!read.Found || string.IsNullOrWhiteSpace(read.Content))
            return (null, null);
        if (!manifestFile.TryParse(read.Content, out var manifest, out var parseError) || manifest is null)
            return (null, $"{manifestFile.FileName} could not be read: {parseError}");

        return (manifest.Ai, null);
    }

    public async Task<OperationResult> WriteCodeGraphConfigAsync(
        int workspaceId,
        WorkspaceFeatureContextId? contextId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var targetContextId = contextId
                ?? await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
            var args = await pathResolver.GetWorkerArgsAsync(targetContextId, cancellationToken);
            if (args.WorkspaceRepositoryName is null)
                return new OperationResult(true, NoWorkspaceRepositoryMessage);

            var read = await WorkspaceRepositoryFileAccess.ReadAsync(
                workerBridge, args, CodeGraphConfigFile.FilePath, cancellationToken);
            if (read.Error is not null)
                return OperationResult.Fail(read.Error);

            var manifest = await BuildFromDatabaseAsync(workspaceId, cancellationToken);
            var content = CodeGraphConfigFile.Apply(read.Content, manifest.Repositories.Select(r => r.Name));
            if (content is null)
                return OperationResult.Fail($"{CodeGraphConfigFile.FilePath} is not a valid JSON object; fix it by hand.");

            return await WorkspaceRepositoryFileAccess.WriteAsync(
                workerBridge, args, CodeGraphConfigFile.FilePath, content, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Writing codegraph.json failed. WorkspaceId={WorkspaceId}", workspaceId);
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<WorkspaceManifestDrift> DetectDriftAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var (contextId, args) = await GetSpecialContextArgsAsync(workspaceId, cancellationToken);

        // D8 point 4: Feature contexts never run drift detection.
        var info = await contextResolver.GetRequiredAsync(contextId, workspaceId, cancellationToken);
        if (!info.IsSpecialWorkspace)
            throw new InvalidOperationException("Workspace definition drift is only detected for the special Workspace context.");

        if (args.WorkspaceRepositoryName is null)
        {
            await PersistDriftAsync(workspaceId, hasDrift: false, cancellationToken);
            return NoDrift();
        }

        var read = await WorkspaceRepositoryFileAccess.ReadAsync(
            workerBridge, args, manifestFile.FileName, cancellationToken);
        if (read.Error is not null)
            throw new InvalidOperationException(read.Error);

        WorkspaceManifestDrift drift;
        if (!read.Found)
        {
            drift = ParseFailure($"{manifestFile.FileName} was not found in the Workspace repository");
        }
        else if (!manifestFile.TryParse(read.Content ?? string.Empty, out var fileManifest, out var parseError) || fileManifest is null)
        {
            drift = ParseFailure(parseError ?? "Workspace definition could not be read");
        }
        else
        {
            var databaseManifest = await BuildFromDatabaseAsync(workspaceId, cancellationToken);
            drift = Compare(fileManifest, databaseManifest);
        }

        await PersistDriftAsync(workspaceId, drift.HasDrift, cancellationToken);
        return drift;
    }

    private static WorkspaceManifestDrift Compare(WorkspaceManifest file, WorkspaceManifest database)
    {
        var added = new List<string>();
        var removed = new List<string>();

        var databaseByUrl = ByRepositoryUrl(database.Repositories);
        var fileByUrl = ByRepositoryUrl(file.Repositories);
        foreach (var (url, repository) in fileByUrl.OrderBy(p => p.Value.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!databaseByUrl.ContainsKey(url))
                added.Add(repository.Name);
        }

        foreach (var (url, repository) in databaseByUrl.OrderBy(p => p.Value.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!fileByUrl.ContainsKey(url))
                removed.Add(repository.Name);
        }

        var fileConnectors = NormalizedConnectorUrls(file.Connectors);
        var databaseConnectors = NormalizedConnectorUrls(database.Connectors);
        var addedConnectors = fileConnectors.Except(databaseConnectors, StringComparer.OrdinalIgnoreCase).ToList();
        var removedConnectors = databaseConnectors.Except(fileConnectors, StringComparer.OrdinalIgnoreCase).ToList();

        var changedProfile = new List<string>();
        var fileProfile = file.Workspace.Profile;
        var databaseProfile = database.Workspace.Profile;
        if (!string.Equals(fileProfile.Type, databaseProfile.Type, StringComparison.OrdinalIgnoreCase))
            changedProfile.Add("type");
        if (!string.Equals(fileProfile.Versioning, databaseProfile.Versioning, StringComparison.OrdinalIgnoreCase))
            changedProfile.Add("versioning");
        if (!string.Equals(fileProfile.Ci, databaseProfile.Ci, StringComparison.OrdinalIgnoreCase))
            changedProfile.Add("ci");

        var hasDrift = added.Count > 0 || removed.Count > 0 || changedProfile.Count > 0
            || addedConnectors.Count > 0 || removedConnectors.Count > 0;
        return new WorkspaceManifestDrift(hasDrift, null, added, removed, changedProfile, addedConnectors, removedConnectors);
    }

    private static Dictionary<string, WorkspaceManifestRepository> ByRepositoryUrl(IReadOnlyList<WorkspaceManifestRepository> repositories)
    {
        var result = new Dictionary<string, WorkspaceManifestRepository>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in repositories)
            result.TryAdd(RepositoryUrlIdentity.NormalizeRepositoryUrl(repository.RepositoryUrl), repository);
        return result;
    }

    private static List<string> NormalizedConnectorUrls(IReadOnlyList<WorkspaceManifestConnector> connectors) =>
        connectors
            .Select(c => RepositoryUrlIdentity.NormalizeConnectorUrl(c.Url))
            .Where(url => url.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(url => url, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static WorkspaceManifestDrift NoDrift() => new(false, null, [], [], [], [], []);

    private static WorkspaceManifestDrift ParseFailure(string reason) => new(true, reason, [], [], [], [], []);

    private async Task PersistDriftAsync(int workspaceId, bool hasDrift, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, cancellationToken);
        if (workspace is null)
            return;

        workspace.ManifestDriftDetectedAt = hasDrift ? DateTime.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<(WorkspaceFeatureContextId ContextId, WorkerWorkspaceArgs Args)> GetSpecialContextArgsAsync(
        int workspaceId,
        CancellationToken cancellationToken)
    {
        var contextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        var args = await pathResolver.GetWorkerArgsAsync(contextId, cancellationToken);
        return (contextId, args);
    }

    private static string ConnectorUrl(Connector? connector) =>
        RepositoryUrlIdentity.NormalizeConnectorUrl(
            RepositoryUrlHelper.GetWebRootFromConnectorApiBase(connector?.ApiBaseUrl));
}

/// <summary>
/// Worker file access for the Workspace repository root, shared by <see cref="WorkspaceManifestService"/> and the
/// Workspace-repository operations. Reads use <c>GetFileContents</c>, writes use <c>WriteRepositoryFile</c> (D5).
/// </summary>
internal static class WorkspaceRepositoryFileAccess
{
    public const string GitIgnoreFilePath = ".gitignore";

    private const string NotFoundPrefix = "File not found";

    /// <summary>
    /// Reads a file of the Workspace repository. <c>Found</c> is false for a missing file (no error); any other
    /// failure is reported in <c>Error</c>.
    /// </summary>
    public static async Task<(bool Found, string? Content, string? Error)> ReadAsync(
        IWorkerBridge workerBridge,
        WorkerWorkspaceArgs args,
        string filePath,
        CancellationToken cancellationToken)
    {
        var response = await workerBridge.SendCommandAsync(
            "GetFileContents",
            new
            {
                workspaceName = args.WorkspaceFolderName,
                workspaceRoot = args.WorkspaceRoot,
                workspaceRepositoryName = args.WorkspaceRepositoryName,
                repositoryName = args.WorkspaceRepositoryName,
                filePath,
            },
            cancellationToken);

        if (!response.Success)
            return (false, null, response.Error ?? $"Could not read {filePath}.");

        var data = WorkerResponseJson.DeserializeWorkerResponse<WorkerGetFileContentsResponse>(response.Data);
        if (data is null)
            return (false, null, $"The Worker returned an unreadable response for {filePath}.");

        if (!string.IsNullOrWhiteSpace(data.ErrorMessage))
        {
            return data.ErrorMessage.StartsWith(NotFoundPrefix, StringComparison.OrdinalIgnoreCase)
                ? (false, null, null)
                : (false, null, data.ErrorMessage);
        }

        return (true, data.Content ?? string.Empty, null);
    }

    public static async Task<OperationResult> WriteAsync(
        IWorkerBridge workerBridge,
        WorkerWorkspaceArgs args,
        string filePath,
        string content,
        CancellationToken cancellationToken)
    {
        var workspaceRepositoryName = args.WorkspaceRepositoryName
            ?? throw new InvalidOperationException("The Workspace has no Workspace repository.");

        var response = await workerBridge.SendCommandAsync(
            WorkerHubMethods.WriteRepositoryFile,
            new
            {
                workspaceName = args.WorkspaceFolderName,
                workspaceRoot = args.WorkspaceRoot,
                workspaceRepositoryName,
                repositoryName = workspaceRepositoryName,
                filePath,
                content,
                onlyIfChanged = true,
            },
            cancellationToken);

        if (!response.Success)
            return OperationResult.Fail(response.Error ?? $"Could not write {filePath}.");

        var data = WorkerResponseJson.DeserializeWorkerResponse<WriteRepositoryFileWorkerResponse>(response.Data);
        if (data is { Success: false })
            return OperationResult.Fail(data.ErrorMessage ?? $"Could not write {filePath}.");

        return OperationResult.Ok();
    }

    internal sealed class WriteRepositoryFileWorkerResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("success")]
        public bool Success { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("written")]
        public bool Written { get; set; }
    }
}

/// <summary>Call-site helpers for the membership, profile and drift hooks (plan Unit D steps 6 and 7).</summary>
public static class WorkspaceManifestHooks
{
    /// <summary>
    /// Regenerates the managed <c>.gitignore</c> section, then the Workspace definition, then <c>codegraph.json</c> when the
    /// definition has <c>"codegraph": true</c>. Does nothing when the
    /// Workspace has no Workspace repository. Never throws; returns the first failure message for the caller to
    /// show as a toast, or null when everything was written (or there was nothing to write).
    /// </summary>
    public static async Task<string?> SyncDefinitionToDiskAsync(
        this IWorkspaceManifestService manifestService,
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var gitIgnore = await manifestService.WriteManagedGitIgnoreAsync(workspaceId, cancellationToken);
            if (!gitIgnore.Success)
                return $"Could not update .gitignore in the Workspace repository: {gitIgnore.Error}";

            var manifest = await manifestService.WriteAuthoritativeManifestAsync(workspaceId, cancellationToken);
            if (!manifest.Success)
                return $"Could not write the Workspace definition: {manifest.Error}";

            if (await manifestService.IsCodeGraphEnabledAsync(workspaceId, cancellationToken))
            {
                var codeGraph = await manifestService.WriteCodeGraphConfigAsync(workspaceId, cancellationToken: cancellationToken);
                if (!codeGraph.Success)
                    return $"Could not update codegraph.json in the Workspace repository: {codeGraph.Error}";
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Could not write the Workspace definition: {ex.Message}";
        }
    }

    /// <summary>
    /// Fire-and-forget drift detection with its own DI scope (AGENTS.md rule 3), so it never reuses a circuit's
    /// DbContext and never outlives a disposed scope. Failures are logged and swallowed.
    /// </summary>
    public static void DetectDriftInBackground(IServiceScopeFactory scopeFactory, ILogger logger, int workspaceId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var manifestService = scope.ServiceProvider.GetRequiredService<IWorkspaceManifestService>();
                await manifestService.DetectDriftAsync(workspaceId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Workspace definition drift detection failed. WorkspaceId={WorkspaceId}", workspaceId);
            }
        });
    }
}
