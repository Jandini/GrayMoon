using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application;
using GrayMoon.Application.Workspaces;
using GrayMoon.Common.Git;
using GrayMoon.Common.Manifest;
using Microsoft.EntityFrameworkCore;
using WorkspaceEntity = GrayMoon.App.Models.Workspace;

namespace GrayMoon.App.Services.Application;

/// <summary>
/// Import from disk (<see cref="IWorkspaceImportOperations"/>). Shares Restore's steps for a folder that is a
/// Workspace repository with a definition (the definition decides profile, Source repositories, read only flags and
/// version files); the difference is that the repository in the folder is adopted, not cloned. A folder without a
/// definition gets the profile the user picked and the repositories in it that GrayMoon knows.
/// </summary>
public sealed partial class WorkspaceRepositoryOperations
{
    internal const string NoRepositoriesMessage = "This folder contains no Git repositories.";

    internal const string NothingKnownMessage =
        "None of the repositories in this folder are imported into GrayMoon. Import them through Connectors, then import this folder.";

    internal static string WorkspaceRepositoryNotImportedMessage(string? origin) =>
        string.IsNullOrWhiteSpace(origin)
            ? "The folder is a Git repository with a Workspace definition, but it has no origin remote, so GrayMoon cannot tell which repository it is."
            : $"The Workspace repository {origin} is not imported into GrayMoon. Import it through Connectors, then import this folder.";

    internal const string RepositoryWithoutDefinitionNote =
        "This folder is a Git repository without a Workspace definition (" + WorkspaceManifestFile.FileName + "). It is not added as the "
        + "Workspace repository and nothing is written into it. You can choose it as the Workspace repository in the workspace settings afterwards.";

    public async Task<ImportableFolderList> ListImportableFoldersAsync(CancellationToken cancellationToken = default)
    {
        if (workerBridge.GetUnavailableReason() is { } unavailable)
            return ImportableFolderList.Failed(unavailable);

        var root = await workspaceService.GetRootPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
            return ImportableFolderList.Failed("Workspace root path is not configured. Go to Settings to configure it.");

        var response = await workerBridge.SendCommandAsync(
            WorkerHubMethods.ListWorkspaceRootFolders, new { workspaceRoot = root }, cancellationToken);
        if (!response.Success)
            return ImportableFolderList.Failed(response.Error ?? "The Worker could not list the workspace root.");

        var data = WorkerResponseJson.DeserializeWorkerResponse<RootFoldersWorkerResponse>(response.Data);
        if (data is null)
            return ImportableFolderList.Failed("The Worker returned an unreadable response for the workspace root.");

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var taken = (await db.Workspaces.AsNoTracking().Select(w => w.Name).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = (data.Folders ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name) && !taken.Contains(name))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ImportableFolderList(folders, null);
    }

    public async Task<ImportWorkspacePreflight> PreflightImportAsync(string folderName, CancellationToken cancellationToken = default)
    {
        try
        {
            var (plan, failure) = await PlanImportAsync(folderName, cancellationToken);
            return plan?.Preview ?? failure!;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Import preflight threw. Folder={Folder}", folderName);
            return ImportWorkspacePreflight.Failed(ex.Message);
        }
    }

    public async Task<ImportWorkspaceResult> ImportFromDiskAsync(
        string folderName,
        ImportWorkspaceOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Checked again here: the folder may have changed since the dialog looked. Nothing has been created yet.
        ImportPlan plan;
        try
        {
            progress.Report("Checking folder...");
            var (checkedPlan, failure) = await PlanImportAsync(folderName, cancellationToken);
            if (checkedPlan is null)
                return ImportWorkspaceResult.Failed(failure!.Error ?? "The folder cannot be imported.");
            plan = checkedPlan;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Import check threw. Folder={Folder}", folderName);
            return ImportWorkspaceResult.Failed(ex.Message);
        }

        progress.Report("Preparing Workspace...");
        WorkspaceEntity workspace;
        try
        {
            workspace = plan.Definition is null
                ? await workspaceRepository.AddAsync(plan.Name, [], options.Type, options.VersioningMode, options.CiProvider)
                // The definition's profile is applied after the repository is adopted, exactly as a restore does.
                : await workspaceRepository.AddAsync(plan.Name, [], WorkspaceType.Basic, WorkspaceVersioningMode.None, WorkspaceCiProvider.None);
        }
        catch (InvalidOperationException ex)
        {
            return ImportWorkspaceResult.Failed(ex.Message);
        }

        var workspaceId = workspace.WorkspaceId;
        try
        {
            var result = plan.Definition is null
                ? await ImportPlainFolderAsync(workspaceId, plan, progress, cancellationToken)
                : await ImportWorkspaceRepositoryAsync(workspaceId, plan, progress, cancellationToken);
            if (result.Success)
            {
                logger.LogInformation(
                    "Persistence: imported Workspace from disk. WorkspaceId={WorkspaceId} Name={Name} Shape={Shape} Linked={Linked} ToClone={ToClone}",
                    workspaceId, plan.Name, plan.Preview.Shape, plan.Preview.LinkedOnDisk.Count, plan.Preview.ToClone.Count);
                return result;
            }

            var residue = await RollbackImportAsync(workspaceId);
            return ImportWorkspaceResult.Failed(result.Error ?? "The import failed.", residue);
        }
        catch (OperationCanceledException)
        {
            await RollbackImportAsync(workspaceId);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Import failed after the Workspace was created. WorkspaceId={WorkspaceId}", workspaceId);
            var residue = await RollbackImportAsync(workspaceId);
            return ImportWorkspaceResult.Failed(ex.Message, residue);
        }
    }

    /// <summary>The folder holds its own definition: Restore's steps, with the repository adopted in place.</summary>
    private async Task<ImportWorkspaceResult> ImportWorkspaceRepositoryAsync(
        int workspaceId, ImportPlan plan, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var definition = plan.Definition!;
        var (restored, error) = await RestoreIntoWorkspaceAsync(
            workspaceId, definition.RootRepositoryId, branchName: null, definition.Context, progress, cancellationToken, adoptExisting: true);
        if (restored is null)
            return ImportWorkspaceResult.Failed(error ?? "The import failed.");

        var warnings = new List<string>();
        if (restored.Warning is not null)
            warnings.Add(restored.Warning);

        // The folders already exist and may hold commits that are only on this machine: a repository that would
        // start read only with such commits is kept writable and reported.
        if (referenceImportGuard is not null && plan.LinkedOnDiskIds.Count > 0)
            warnings.AddRange(await referenceImportGuard.ApplyAsync(workspaceId, plan.LinkedOnDiskIds, cancellationToken));

        return new ImportWorkspaceResult
        {
            Success = true,
            WorkspaceId = workspaceId,
            Warnings = warnings,
            UnresolvedConnectors = restored.UnresolvedConnectors,
            UnresolvedRepositories = restored.UnresolvedRepositories,
        };
    }

    /// <summary>No definition: the repositories on disk that GrayMoon knows are linked. Nothing is written to the folder.</summary>
    private async Task<ImportWorkspaceResult> ImportPlainFolderAsync(
        int workspaceId, ImportPlan plan, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        progress.Report(plan.LinkedOnDiskIds.Count == 1 ? "Linking 1 repository..." : $"Linking {plan.LinkedOnDiskIds.Count} repositories...");
        await workspaceRepository.AddRepositoriesAsync(workspaceId, plan.LinkedOnDiskIds, cancellationToken);
        return new ImportWorkspaceResult { Success = true, WorkspaceId = workspaceId };
    }

    /// <summary>
    /// Takes back a Workspace whose import failed: the Git hooks the adopt wrote, then the rows. The folder and its
    /// content are never deleted or changed. Never throws; returns what could not be taken back.
    /// </summary>
    private async Task<string?> RollbackImportAsync(int workspaceId)
    {
        var residue = new List<string>();
        try
        {
            var detached = await detacher.DetachAsync(workspaceId, null, workspaceRemoved: true, cancellationToken: CancellationToken.None);
            if (!detached.Success)
                residue.Add(DetachResidue(detached));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Import rollback could not remove the Git hooks. WorkspaceId={WorkspaceId}", workspaceId);
            residue.Add($"The Git hooks GrayMoon installed could not be removed: {ex.Message}");
        }

        try
        {
            await DeleteWorkspaceAsync(workspaceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import rollback could not delete the Workspace. WorkspaceId={WorkspaceId}", workspaceId);
            residue.Add($"The Workspace could not be removed from GrayMoon: {ex.Message}");
        }

        logger.LogInformation("Import rolled back. WorkspaceId={WorkspaceId} ResidueCount={ResidueCount}", workspaceId, residue.Count);
        return residue.Count == 0 ? null : string.Join(" ", residue);
    }

    /// <summary>Reads the folder through the Worker and decides what an import would do. Changes nothing.</summary>
    private async Task<(ImportPlan? Plan, ImportWorkspacePreflight? Failure)> PlanImportAsync(string folderName, CancellationToken cancellationToken)
    {
        static (ImportPlan?, ImportWorkspacePreflight?) Fail(string error, bool needsConnectors = false) =>
            (null, ImportWorkspacePreflight.Failed(error, needsConnectors));

        var name = folderName?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return Fail("Choose a folder.");

        if (workerBridge.GetUnavailableReason() is { } unavailable)
            return Fail(unavailable);

        var root = await workspaceService.GetRootPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
            return Fail("Workspace root path is not configured. Go to Settings to configure it.");

        List<CatalogRepository> catalog;
        List<Connector> connectors;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            if (await db.Workspaces.AnyAsync(w => w.Name.ToLower() == name.ToLower(), cancellationToken))
                return Fail($"A workspace named '{name}' already exists.");

            catalog = (await db.Repositories.AsNoTracking()
                    .Select(r => new { r.RepositoryId, r.RepositoryName, r.OrgName, r.CloneUrl })
                    .ToListAsync(cancellationToken))
                .Select(r => new CatalogRepository(r.RepositoryId, r.RepositoryName, r.OrgName, r.CloneUrl))
                .ToList();
            connectors = await db.Connectors.AsNoTracking()
                .Where(c => c.ConnectorType == ConnectorType.GitHub && c.RemovedAtUtc == null)
                .ToListAsync(cancellationToken);
        }

        var response = await workerBridge.SendCommandAsync(
            WorkerHubMethods.InspectWorkspaceFolder, new { workspaceName = name, workspaceRoot = root }, cancellationToken);
        if (!response.Success)
            return Fail(response.Error ?? "The Worker could not read the folder.");
        var folder = WorkerResponseJson.DeserializeWorkerResponse<InspectFolderWorkerResponse>(response.Data);
        if (folder is null)
            return Fail("The Worker returned an unreadable response for the folder.");
        if (!folder.Exists)
            return Fail($"The folder '{name}' does not exist under the workspace root.");

        var children = (folder.Repositories ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
        if (folder.RootRepository is null && children.Count == 0)
            return Fail(NoRepositoriesMessage);

        // Each repository on disk, matched to an imported repository by its origin URL. A folder whose name is not
        // the repository's name cannot be used: GrayMoon looks for a repository in the folder named after it.
        var byId = catalog.ToDictionary(c => c.RepositoryId);
        var catalogUrls = catalog.Select(c => (c.RepositoryId, c.CloneUrl)).ToList();
        var onDisk = new Dictionary<int, string>();
        var notLinked = new List<string>();
        foreach (var child in children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var matched = ExistingWorkspaceFolder.MatchRepositoryId(child.OriginUrl, catalogUrls);
            if (matched is not int repositoryId)
            {
                notLinked.Add(string.IsNullOrWhiteSpace(child.OriginUrl)
                    ? $"{child.Name}: it has no origin remote"
                    : $"{child.Name}: {child.OriginUrl} is not imported into GrayMoon");
                continue;
            }

            var known = byId[repositoryId];
            if (!string.Equals(known.RepositoryName, child.Name, StringComparison.OrdinalIgnoreCase))
            {
                notLinked.Add($"{child.Name}: it is {known.DisplayName}, which GrayMoon expects in a folder named '{known.RepositoryName}'");
                continue;
            }

            if (!onDisk.TryAdd(repositoryId, child.Name!))
                notLinked.Add($"{child.Name}: {known.DisplayName} is already in the folder '{onDisk[repositoryId]}'");
        }

        var hasDefinition = folder.RootRepository is not null && folder.DefinitionFound;
        if (!hasDefinition)
        {
            if (onDisk.Count == 0)
                return (null, ImportWorkspacePreflight.Failed(NothingKnownMessage, needsConnectors: true) with { NotLinked = notLinked });

            var isRepository = folder.RootRepository is not null;
            var plain = new ImportWorkspacePreflight
            {
                Success = true,
                Shape = isRepository ? ImportFolderShape.RepositoryWithoutDefinition : ImportFolderShape.PlainFolder,
                LinkedOnDisk = onDisk.Keys.Select(id => byId[id].DisplayName).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                NotLinked = notLinked,
                Note = isRepository ? RepositoryWithoutDefinitionNote : null,
            };
            return (new ImportPlan(name, plain, onDisk.Keys.Order().ToList(), null), null);
        }

        if (folder.DefinitionError is not null)
            return Fail(folder.DefinitionError);

        var rootOrigin = folder.RootRepository!.OriginUrl;
        if (ExistingWorkspaceFolder.MatchRepositoryId(rootOrigin, catalogUrls) is not int rootRepositoryId)
            return Fail(WorkspaceRepositoryNotImportedMessage(rootOrigin), needsConnectors: true);

        var rootRepository = byId[rootRepositoryId];
        var context = new RestoreContext(
            rootRepository.CloneUrl,
            connectors,
            catalog.Select(c => new RestoreCatalogEntry(c.RepositoryId, c.CloneUrl)).ToList());
        var (definition, definitionError) = RestoreDefinitionEvaluator.Evaluate(
            manifestFile, folder.DefinitionContent ?? string.Empty, context.CloneUrl, context.Connectors, context.Catalog);
        if (definition is null)
            return Fail(definitionError ?? "The Workspace definition is not valid.");

        var listed = definition.SourceRepositoryIds.ToHashSet();
        var linkedIds = listed.Where(onDisk.ContainsKey).Order().ToList();
        notLinked.AddRange(onDisk
            .Where(pair => !listed.Contains(pair.Key) && pair.Key != rootRepositoryId)
            .OrderBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Value}: it is not listed in {WorkspaceManifestFile.FileName}"));

        var preview = new ImportWorkspacePreflight
        {
            Success = true,
            Shape = ImportFolderShape.WorkspaceRepository,
            WorkspaceType = definition.Type,
            VersioningMode = definition.Versioning,
            CiProvider = definition.Ci,
            WorkspaceRepository = rootRepository.DisplayName,
            LinkedOnDisk = linkedIds.Select(id => byId[id].DisplayName).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            ToClone = listed.Where(id => !onDisk.ContainsKey(id)).Select(id => byId[id].DisplayName).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            NotLinked = notLinked,
            MissingConnectors = definition.MissingConnectors,
            MissingRepositories = definition.MissingRepositories,
            ReadOnlyCount = definition.ReferenceRepositoryCount,
        };
        return (new ImportPlan(name, preview, linkedIds, new ImportDefinition(rootRepositoryId, context)), null);
    }

    /// <summary>What the checked folder resolves to. <see cref="Definition"/> is null for a folder without a definition.</summary>
    private sealed record ImportPlan(
        string Name,
        ImportWorkspacePreflight Preview,
        IReadOnlyList<int> LinkedOnDiskIds,
        ImportDefinition? Definition);

    private sealed record ImportDefinition(int RootRepositoryId, RestoreContext Context);

    private sealed record CatalogRepository(int RepositoryId, string RepositoryName, string? OrgName, string CloneUrl)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(OrgName) ? RepositoryName : $"{OrgName}/{RepositoryName}";
    }

    private sealed class RootFoldersWorkerResponse
    {
        public List<string>? Folders { get; set; }
    }

    private sealed class InspectFolderWorkerResponse
    {
        public bool Exists { get; set; }
        public InspectFolderRepository? RootRepository { get; set; }
        public List<InspectFolderRepository>? Repositories { get; set; }
        public bool DefinitionFound { get; set; }
        public string? DefinitionContent { get; set; }
        public string? DefinitionError { get; set; }
    }

    private sealed class InspectFolderRepository
    {
        public string? Name { get; set; }
        public string? OriginUrl { get; set; }
    }
}
