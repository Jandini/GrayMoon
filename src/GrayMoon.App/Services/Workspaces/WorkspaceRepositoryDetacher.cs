using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Workspaces;

/// <summary>What the Worker did for one repository that left a Workspace.</summary>
public sealed record DetachRepositoryOutcome(
    int RepositoryId,
    string RepositoryName,
    string? Path,
    string Outcome,
    string? FailureKind,
    string? FailedPath,
    string? ErrorMessage,
    IReadOnlyList<string> RestoredHooks,
    IReadOnlyList<string> LeftoverReplacedHooks)
{
    public bool Failed => string.Equals(Outcome, DetachOutcomes.Failed, StringComparison.Ordinal);
}

/// <summary>Outcome and failure names as the Worker sends them.</summary>
public static class DetachOutcomes
{
    public const string Failed = "Failed";
    public const string HookNotDeleted = "HookNotDeleted";
    public const string HookNotRestored = "HookNotRestored";
    public const string WorkerBusy = "WorkerBusy";
    public const string GitFailed = "GitFailed";
    public const string InvalidName = "InvalidName";
}

/// <summary>
/// Result of detaching repositories. <see cref="Success"/> false means nothing may be deleted in the database:
/// <see cref="Unavailable"/> says the Worker could not be asked at all, otherwise <see cref="Repositories"/> names
/// the ones that failed.
/// </summary>
public sealed record DetachSummary(
    bool Success,
    string? Unavailable,
    IReadOnlyList<DetachRepositoryOutcome> Repositories,
    IReadOnlyList<string> ReleasedWatchers)
{
    public static DetachSummary Nothing { get; } = new(true, null, [], []);

    public static DetachSummary NotAvailable(string reason) => new(false, reason, [], []);

    public IReadOnlyList<DetachRepositoryOutcome> Failed => Repositories.Where(r => r.Failed).ToList();
}

/// <summary>
/// The one place the App tells the Worker "these repositories leave this Workspace". The Worker then lets go of its
/// file watchers on their folders and removes the Git hooks GrayMoon installed there (<c>DetachRepositories</c>).
/// Every path that deletes a Workspace link row calls this first: the rows are the only record of which folders
/// GrayMoon managed, so they go only after the folders are clean. Changes nothing in the database.
/// </summary>
public sealed class WorkspaceRepositoryDetacher(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IWorkerBridge workerBridge,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceFeatureContextResolver contextResolver,
    IWorkspaceGitChangesMonitoringPause monitoringPause,
    ILogger<WorkspaceRepositoryDetacher> logger)
{
    /// <summary>
    /// Detaches the given links of <paramref name="workspaceId"/> (all of them when <paramref name="workspaceRepositoryIds"/>
    /// is null). When that succeeds, <paramref name="whileDetached"/> runs before Git Changes monitoring of the
    /// Workspace resumes, so the caller's row delete cannot be overtaken by a watcher renewal. It does not run on failure.
    /// </summary>
    public async Task<DetachSummary> DetachAsync(
        int workspaceId,
        IReadOnlyCollection<int>? workspaceRepositoryIds,
        bool workspaceRemoved,
        Func<Task>? whileDetached = null,
        CancellationToken cancellationToken = default)
    {
        List<(int RepositoryId, string RepositoryName)> repositories;
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var query = db.WorkspaceRepositories.AsNoTracking().Where(l => l.WorkspaceId == workspaceId);
            if (workspaceRepositoryIds is not null)
                query = query.Where(l => workspaceRepositoryIds.Contains(l.WorkspaceRepositoryId));
            var rows = await query
                .Select(l => new { l.RepositoryId, l.Repository!.RepositoryName })
                .ToListAsync(cancellationToken);
            repositories = rows.Select(r => (r.RepositoryId, r.RepositoryName)).ToList();
        }

        if (repositories.Count == 0 && !workspaceRemoved)
        {
            if (whileDetached is not null)
                await whileDetached();
            return DetachSummary.Nothing;
        }

        return await SendAsync(workspaceId, repositories, workspaceRemoved, keepWorkspaceRepositoryName: true, whileDetached, cancellationToken);
    }

    /// <summary>
    /// Detaches the Source folder <c>&lt;workspace&gt;/&lt;repositoryName&gt;</c> itself, even when the Workspace repository
    /// now has that name (a Source that became the Workspace repository leaves its old folder behind).
    /// </summary>
    public Task<DetachSummary> DetachSourceFolderAsync(int workspaceId, int repositoryId, string repositoryName, CancellationToken cancellationToken = default) =>
        SendAsync(workspaceId, [(repositoryId, repositoryName)], workspaceRemoved: false, keepWorkspaceRepositoryName: false, whileDetached: null, cancellationToken);

    private async Task<DetachSummary> SendAsync(
        int workspaceId,
        List<(int RepositoryId, string RepositoryName)> repositories,
        bool workspaceRemoved,
        bool keepWorkspaceRepositoryName,
        Func<Task>? whileDetached,
        CancellationToken cancellationToken)
    {
        if (workerBridge.GetUnavailableReason() is { } unavailable)
            return DetachSummary.NotAvailable(unavailable);

        var specialContextId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        var args = await pathResolver.GetWorkerArgsAsync(specialContextId, cancellationToken);
        var contexts = await contextResolver.ListForWorkspaceAsync(workspaceId, cancellationToken);

        // The keeper renews watchers of every context on a timer; while these are paused it cannot put back a watcher
        // the Worker is about to release.
        var pauses = contexts.Select(c => monitoringPause.Pause(c.ContextId.Value)).ToList();
        try
        {
            var response = await workerBridge.SendCommandAsync(
                WorkerHubMethods.DetachRepositories,
                new
                {
                    workspaceName = args.WorkspaceFolderName,
                    workspaceRoot = args.WorkspaceRoot,
                    workspaceRepositoryName = keepWorkspaceRepositoryName ? args.WorkspaceRepositoryName : null,
                    workspaceId,
                    workspaceRemoved,
                    repositories = repositories.Select(r => new { repositoryId = r.RepositoryId, repositoryName = r.RepositoryName }).ToList(),
                },
                cancellationToken);

            if (!response.Success)
                return DetachSummary.NotAvailable(response.Error ?? "The Worker could not clean up the repositories.");

            var data = WorkerResponseJson.DeserializeWorkerResponse<DetachRepositoriesWorkerResponse>(response.Data);
            if (data is null)
                return DetachSummary.NotAvailable("The Worker returned an unreadable response for the clean-up.");

            var nameById = repositories.ToDictionary(r => r.RepositoryId, r => r.RepositoryName);
            var outcomes = (data.Results ?? [])
                .Select(r => new DetachRepositoryOutcome(
                    r.RepositoryId,
                    r.RepositoryName ?? nameById.GetValueOrDefault(r.RepositoryId) ?? $"repository {r.RepositoryId}",
                    r.Path,
                    r.Outcome ?? string.Empty,
                    r.FailureKind,
                    r.FailedPath,
                    r.ErrorMessage,
                    r.RestoredHooks ?? [],
                    r.LeftoverReplacedHooks ?? []))
                .ToList();
            var summary = new DetachSummary(data.Success && !outcomes.Any(o => o.Failed), null, outcomes, data.ReleasedWatchers ?? []);

            logger.LogInformation(
                "Detached repositories. WorkspaceId={WorkspaceId} WorkspaceRemoved={WorkspaceRemoved} Requested={Requested} Failed={Failed} ReleasedWatchers={ReleasedWatchers}",
                workspaceId, workspaceRemoved, repositories.Count, summary.Failed.Count, summary.ReleasedWatchers.Count);

            if (summary.Success && whileDetached is not null)
                await whileDetached();
            return summary;
        }
        finally
        {
            foreach (var pause in pauses)
                pause.Dispose();
        }
    }

    private sealed class DetachRepositoriesWorkerResponse
    {
        public bool Success { get; set; }
        public List<DetachRepositoriesWorkerResult>? Results { get; set; }
        public List<string>? ReleasedWatchers { get; set; }
    }

    private sealed class DetachRepositoriesWorkerResult
    {
        public int RepositoryId { get; set; }
        public string? RepositoryName { get; set; }
        public string? Path { get; set; }
        public string? Outcome { get; set; }
        public string? FailureKind { get; set; }
        public string? FailedPath { get; set; }
        public string? ErrorMessage { get; set; }
        public List<string>? RestoredHooks { get; set; }
        public List<string>? LeftoverReplacedHooks { get; set; }
    }
}

/// <summary>
/// The text a user reads when detaching did not work, and the notes after it did. One place, so Remove Workspace,
/// the Repositories dialog and Workspace settings say the same thing: nothing was removed from GrayMoon, which
/// folder stopped it and why, and what to do.
/// </summary>
public static class DetachFailureText
{
    public const string WorkspaceNotRemoved = "The workspace was not removed.";
    public const string RepositoriesNotRemoved = "The repositories were not removed from the workspace.";
    public const string WorkspaceRepositoryNotDisabled = "The Workspace repository was not disabled.";

    public const string RetryRemoveWorkspace = "press Remove again";
    public const string RetrySave = "save again";

    /// <summary>The failure as an <see cref="OperationResult"/>: the headline in <c>Error</c>, one line per repository in <c>RepoErrors</c>.</summary>
    public static OperationResult ToFailure(DetachSummary summary, string notDone, string retry)
    {
        if (summary.Unavailable is { } unavailable)
            return OperationResult.Fail($"{notDone} {WorkerNeeded(unavailable)}");

        var failed = summary.Failed;
        var total = summary.Repositories.Count;
        var headline =
            $"{notDone} GrayMoon could not finish cleaning up {failed.Count} of {total} {(total == 1 ? "repository" : "repositories")}. "
            + $"Fix the problem below and {retry}.";
        var cleaned = total - failed.Count;
        if (cleaned > 0)
        {
            headline += cleaned == 1
                ? " The other repository is already cleaned up; it gets its hooks back on the next Sync."
                : $" The other {cleaned} repositories are already cleaned up; they get their hooks back on the next Sync.";
        }

        return OperationResult.Fail(headline, failed.ToDictionary(r => r.RepositoryId, Describe));
    }

    /// <summary>What to tell the user after a successful detach; empty when there is nothing to tell.</summary>
    public static IReadOnlyList<string> SuccessNotes(DetachSummary summary)
    {
        var notes = new List<string>();
        var restored = summary.Repositories.Where(r => r.RestoredHooks.Count > 0).Select(r => r.RepositoryName).ToList();
        if (restored.Count > 0)
        {
            notes.Add(restored.Count == 1
                ? $"Your own Git hooks were put back in 1 repository: {restored[0]}."
                : $"Your own Git hooks were put back in {restored.Count} repositories: {string.Join(", ", restored)}.");
        }

        var leftover = summary.Repositories.Where(r => r.LeftoverReplacedHooks.Count > 0).ToList();
        if (leftover.Count > 0)
        {
            var detail = string.Join("; ", leftover.Select(r => $"{r.RepositoryName} ({string.Join(", ", r.LeftoverReplacedHooks)})"));
            notes.Add(leftover.Count == 1
                ? $"Older replaced hook files were left in 1 repository: {detail}."
                : $"Older replaced hook files were left in {leftover.Count} repositories: {detail}.");
        }

        return notes;
    }

    /// <summary>A failed result as one paragraph, for a dialog that shows a single error line: the headline, then each repository.</summary>
    public static string Flatten(OperationResult result)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.Error))
            parts.Add(result.Error);
        if (result.RepoErrors is { Count: > 0 } repoErrors)
            parts.AddRange(repoErrors.OrderBy(e => e.Key).Select(e => e.Value));
        return parts.Count == 0 ? "The operation failed." : string.Join(" ", parts);
    }

    /// <summary>The reason the Worker is needed, in front of the Worker's own availability message.</summary>
    public static string WorkerNeeded(string unavailableReason) =>
        $"GrayMoon removes its Git hooks from the repositories through the Worker. {unavailableReason}";

    internal static string Describe(DetachRepositoryOutcome repository)
    {
        var detail = string.IsNullOrWhiteSpace(repository.ErrorMessage) ? null : repository.ErrorMessage.Trim().TrimEnd('.');
        var because = detail is null ? "" : $" ({detail})";
        return repository.FailureKind switch
        {
            DetachOutcomes.HookNotDeleted =>
                $"{repository.RepositoryName}: the Git hook file {repository.FailedPath} could not be deleted{because}. "
                + "Close programs that use this repository, or check the file's permissions.",
            DetachOutcomes.HookNotRestored =>
                $"{repository.RepositoryName}: your own hook {repository.FailedPath} could not be renamed back{because}. "
                + "GrayMoon's hook is already gone; rename the file yourself or close the program that has it open.",
            DetachOutcomes.WorkerBusy =>
                $"{repository.RepositoryName}: the Worker is still working in {repository.FailedPath ?? repository.Path ?? "this folder"} and could not stop in time{because}. "
                + "Wait a moment and try again.",
            DetachOutcomes.GitFailed =>
                $"{repository.RepositoryName}: Git could not read the repository in {repository.FailedPath ?? repository.Path ?? "its folder"}{because}. "
                + "Repair the repository, or delete the folder if you no longer need it.",
            DetachOutcomes.InvalidName =>
                $"{repository.RepositoryName}: the repository name is not a valid folder name{because}.",
            _ =>
                $"{repository.RepositoryName}: the clean-up failed{because}.",
        };
    }
}
