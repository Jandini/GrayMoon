using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Policy;

/// <summary>HEAD of the Workspace checkout of a repository, as the activation check saw it.</summary>
public sealed record ReferenceActivationHead(string? Branch, string? Commit);

/// <summary>
/// The outcome of the activation check for a set of repositories. <see cref="Heads"/> holds the Workspace checkout
/// HEAD of every repository whose checkout exists and could be read, keyed by repository id.
/// </summary>
public sealed record ReferenceActivationReport(
    IReadOnlyList<ReferenceActivationFinding> Findings,
    IReadOnlyDictionary<int, ReferenceActivationHead> Heads)
{
    public bool IsBlocked => Findings.Any(finding => finding.Blocks);

    public IReadOnlyList<ReferenceActivationFinding> Blocks => [.. Findings.Where(finding => finding.Blocks)];

    public IReadOnlyList<ReferenceActivationFinding> Warnings => [.. Findings.Where(finding => !finding.Blocks)];
}

/// <summary>
/// The activation check: before a repository becomes read-only, every checkout of it on this machine - the Workspace
/// checkout and each Feature worktree - is inspected through the Worker. It reads only; nothing is fetched and nothing
/// is changed. The Workspace-role repository is never read-only and is left out.
/// </summary>
public sealed class ReferenceActivationChecker(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IWorkerBridge workerBridge,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceFeatureContextResolver contextResolver,
    Microsoft.Extensions.Options.IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<ReferenceActivationChecker> logger)
{
    /// <summary>
    /// Repositories are inspected in parallel, at most <see cref="WorkspaceOptions.MaxParallelOperations"/> at a
    /// time; the checkouts of one repository are inspected one after the other. The findings are in repository
    /// order whatever order the answers arrive in.
    /// </summary>
    public async Task<ReferenceActivationReport> CheckAsync(
        int workspaceId,
        IReadOnlyCollection<int> repositoryIds,
        CancellationToken cancellationToken = default,
        IProgress<OperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(repositoryIds);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var links = await db.WorkspaceRepositories
            .AsNoTracking()
            .Include(link => link.Repository)
            .Where(link => link.WorkspaceId == workspaceId
                && repositoryIds.Contains(link.RepositoryId)
                && link.Role != WorkspaceRepositoryRole.Workspace)
            .OrderBy(link => link.Repository!.RepositoryName)
            .ToListAsync(cancellationToken);

        var findings = new List<ReferenceActivationFinding>();
        var heads = new Dictionary<int, ReferenceActivationHead>();
        if (links.Count == 0)
            return new ReferenceActivationReport(findings, heads);

        var linkIds = links.Select(link => link.WorkspaceRepositoryId).ToList();
        var featureRows = await db.WorkspaceFeatureRepositories
            .AsNoTracking()
            .Where(row => linkIds.Contains(row.WorkspaceRepositoryId)
                && row.State != WorkspaceFeatureRepositoryState.Removed)
            .Select(row => new FeatureCheckout(
                row.WorkspaceRepositoryId,
                row.WorktreePath,
                row.State,
                row.WorkspaceFeatureContext!.WorkspaceFeature!.Name))
            .ToListAsync(cancellationToken);
        var featureRowsByLink = featureRows.ToLookup(row => row.WorkspaceRepositoryId);

        var special = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);

        // Paths first, one after the other: they come from the database.
        var mainPaths = new string?[links.Count];
        for (var i = 0; i < links.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                mainPaths[i] = await pathResolver.GetRepositoryPathAsync(special, links[i].WorkspaceRepositoryId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Activation check: the path of repository {RepositoryId} could not be resolved.", links[i].RepositoryId);
            }
        }

        var perLink = new (List<ReferenceActivationFinding> Findings, ReferenceActivationHead? Head)[links.Count];
        var completed = 0;
        progress?.Report(new OperationProgress(ProgressMessage(0, links.Count), 0, links.Count));
        using var gate = new SemaphoreSlim(Math.Max(1, workspaceOptions.Value.MaxParallelOperations));
        await Task.WhenAll(Enumerable.Range(0, links.Count).Select(async index =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                perLink[index] = await CheckLinkAsync(links[index], mainPaths[index], featureRowsByLink[links[index].WorkspaceRepositoryId], cancellationToken);
            }
            finally
            {
                gate.Release();
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new OperationProgress(ProgressMessage(done, links.Count), done, links.Count));
            }
        }));

        for (var i = 0; i < links.Count; i++)
        {
            findings.AddRange(perLink[i].Findings);
            if (perLink[i].Head is { } head)
                heads[links[i].RepositoryId] = head;
        }

        return new ReferenceActivationReport(findings, heads);
    }

    /// <summary>The start text until the first repository is done, then the count.</summary>
    internal static string ProgressMessage(int completed, int total) =>
        completed <= 0 ? "Checking repositories for local work..." : $"Checked {completed} of {total} repositories";

    private async Task<(List<ReferenceActivationFinding> Findings, ReferenceActivationHead? Head)> CheckLinkAsync(
        WorkspaceRepositoryLink link,
        string? mainPath,
        IEnumerable<FeatureCheckout> featureRows,
        CancellationToken cancellationToken)
    {
        var findings = new List<ReferenceActivationFinding>();
        ReferenceActivationHead? head = null;
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = link.Repository?.RepositoryName ?? string.Empty;
            void Add(string? feature, ReferenceActivationSeverity severity, string message) =>
                findings.Add(new ReferenceActivationFinding(link.RepositoryId, name, feature, severity, Describe(name, feature, message)));

            if (mainPath is null)
            {
                Add(null, ReferenceActivationSeverity.Block, ReferenceActivationRule.UnreadableMessage);
                return (findings, head);
            }

            var main = await InspectAsync(mainPath, mainPath, cancellationToken);
            if (main is { Exists: false }
                && (string.IsNullOrWhiteSpace(main.Error) || string.Equals(main.Error, MainCheckoutMissingError, StringComparison.Ordinal)))
            {
                // Not cloned on this machine: nothing here can be unpushed, and without the clone there are no worktrees.
                Add(null, ReferenceActivationSeverity.Warning, ReferenceActivationRule.MissingFolderMessage);
                return (findings, head);
            }
            else
            {
                var (severity, message) = ReferenceActivationRule.Evaluate(main);
                if (message != null)
                    Add(null, severity, message);
                if (main != null && severity != ReferenceActivationSeverity.Block)
                    head = new ReferenceActivationHead(main.Branch, main.HeadSha);
            }

            foreach (var row in featureRows.OrderBy(row => row.FeatureName, StringComparer.OrdinalIgnoreCase))
            {
                if (row.State != WorkspaceFeatureRepositoryState.Ready)
                {
                    Add(row.FeatureName, ReferenceActivationSeverity.Warning, "its worktree is not ready, so it was not checked.");
                    continue;
                }

                var worktree = await InspectAsync(mainPath, row.WorktreePath, cancellationToken);
                if (worktree is { Exists: false } && string.IsNullOrWhiteSpace(worktree.Error))
                {
                    Add(row.FeatureName, ReferenceActivationSeverity.Warning, ReferenceActivationRule.MissingFolderMessage);
                    continue;
                }

                var (severity, message) = ReferenceActivationRule.Evaluate(worktree);
                if (message != null)
                    Add(row.FeatureName, severity, message);
            }
        }

        return (findings, head);
    }

    /// <summary>
    /// What the Worker's InspectWorktree reports when the main checkout folder does not exist
    /// (<c>GitWorktreeService.InspectWorktreeAsync</c>). A Worker test pins the text.
    /// </summary>
    internal const string MainCheckoutMissingError = "Repository not found.";

    internal static string Describe(string repositoryName, string? featureName, string message) =>
        featureName == null
            ? $"Repository {repositoryName}: {message}"
            : $"Repository {repositoryName} in Feature {featureName}: {message}";

    /// <summary>Null when the Worker gave no usable answer; the rule treats that as unreadable.</summary>
    private async Task<InspectWorktreeWorkerResponse?> InspectAsync(string mainRepositoryPath, string worktreePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mainRepositoryPath) || string.IsNullOrWhiteSpace(worktreePath))
            return null;

        try
        {
            var response = await workerBridge.SendCommandAsync(
                WorkerHubMethods.InspectWorktree,
                new { mainRepositoryPath, worktreePath },
                cancellationToken);
            if (!response.Success)
            {
                logger.LogWarning("Activation check: InspectWorktree failed for {Path}: {Error}", worktreePath, response.Error);
                return null;
            }

            return WorkerResponseJson.DeserializeWorkerResponse<InspectWorktreeWorkerResponse>(response.Data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Activation check: InspectWorktree failed for {Path}", worktreePath);
            return null;
        }
    }

    private sealed record FeatureCheckout(int WorkspaceRepositoryId, string WorktreePath, WorkspaceFeatureRepositoryState State, string FeatureName);
}
