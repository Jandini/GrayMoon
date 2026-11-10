using System.Collections.Concurrent;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Exceptions;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Services.Git;

public sealed partial class WorkspaceGitService
{
    /// <summary>
    /// Fires <c>dotnet restore --force --no-cache &lt;project.csproj&gt;</c> for each specified project file.
    /// A failed restore never interrupts the caller's workflow, but it is never silent either: every repository gets an
    /// outcome, and a failure is logged and written to the running job's terminal as
    /// "Package restore failed for &lt;repo&gt;: &lt;reason&gt;". Empty for a workspace whose profile does not restore packages.
    /// </summary>
    public async Task<IReadOnlyList<PackageRestoreOutcome>> RestoreDependenciesAsync(int workspaceId, WorkspaceFeatureContextId contextId, IEnumerable<(string RepoName, IReadOnlyList<string> ProjectPaths)> repos, CancellationToken cancellationToken)
    {
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null) return [];
        if (!await UsesPackageRestoreAsync(workspaceId, cancellationToken)) return [];
        var workerArgs = await ResolveWorkerPathArgsAsync(workspace.WorkspaceId, contextId, cancellationToken);
        var workspaceRoot = workerArgs.WorkspaceRoot;
        var workspaceFolderName = workerArgs.WorkspaceFolderName;
        var workspaceRepositoryName = workerArgs.WorkspaceRepositoryName;
        var tasks = repos
            .Where(r => r.ProjectPaths.Count > 0)
            .Select(async r =>
            {
                PackageRestoreOutcome outcome;
                try
                {
                    var response = await _workerBridge.SendCommandAsync(
                        "DotnetRestore",
                        new { workspaceName = workspaceFolderName, repositoryName = r.RepoName, projectPaths = r.ProjectPaths, workspaceRoot, workspaceRepositoryName },
                        cancellationToken);
                    outcome = PackageRestoreOutcome.FromResponse(r.RepoName, response);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    outcome = PackageRestoreOutcome.Failed(r.RepoName, ex.Message);
                }

                if (!outcome.Success)
                {
                    _logger.LogWarning("{Warning} Workspace {WorkspaceName}, continuing.", outcome.Warning, workspace.Name);
                    outcome.WriteToJobTerminal();
                }

                return outcome;
            });
        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Fires <c>dotnet restore --force --no-cache</c> for all tracked project files across all workspace
    /// repositories, skipping repos pinned to a tag. Best-effort: individual restore errors are logged and swallowed.
    /// Returns the total number of project files targeted for restore, which is 0 for a workspace whose
    /// profile does not restore packages.
    /// </summary>
    public async Task<int> RestoreAllWorkspacePackagesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        Action<string> setProgress,
        CancellationToken cancellationToken)
    {
        if (!_workerBridge.IsWorkerConnected)
            throw new WorkerNotConnectedException();

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null || !await UsesPackageRestoreAsync(workspaceId, cancellationToken))
            return 0;

        setProgress("Restoring packages...");

        var tagPinnedIds = workspace.Repositories
            .Where(l => !string.IsNullOrWhiteSpace(l.CheckedOutTag))
            .Select(l => l.RepositoryId)
            .ToHashSet();

        var projects = await _workspaceProjectRepository.GetByWorkspaceIdAsync(workspaceId, (WorkspaceFeatureContextId?)contextId);
        var repoGroups = projects
            .Where(p => p.Repository != null
                        && !string.IsNullOrWhiteSpace(p.ProjectFilePath)
                        && !tagPinnedIds.Contains(p.RepositoryId))
            .GroupBy(p => (p.RepositoryId, RepoName: p.Repository!.RepositoryName))
            .Select(g => (g.Key.RepoName, ProjectPaths: (IReadOnlyList<string>)g.Select(p => p.ProjectFilePath!).ToList()))
            .ToList();

        var totalCount = repoGroups.Sum(r => r.ProjectPaths.Count);
        if (totalCount == 0)
            return 0;

        ReportRestoreFailures(await RestoreDependenciesAsync(workspaceId, contextId, repoGroups, cancellationToken), setProgress);
        return totalCount;
    }

    public async Task<int> RestoreSyncedWorkspacePackagesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlySet<int> syncedRepoIds,
        Action<string> setProgress,
        CancellationToken cancellationToken)
    {
        if (!_workerBridge.IsWorkerConnected)
            throw new WorkerNotConnectedException();

        if (syncedRepoIds.Count == 0)
            return 0;

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId);
        if (workspace == null || !await UsesPackageRestoreAsync(workspaceId, cancellationToken))
            return 0;

        setProgress("Restoring packages...");

        var tagPinnedIds = workspace.Repositories
            .Where(l => !string.IsNullOrWhiteSpace(l.CheckedOutTag))
            .Select(l => l.RepositoryId)
            .ToHashSet();

        var projects = await _workspaceProjectRepository.GetByWorkspaceIdAsync(workspaceId, (WorkspaceFeatureContextId?)contextId);
        var repoGroups = projects
            .Where(p => p.Repository != null
                        && !string.IsNullOrWhiteSpace(p.ProjectFilePath)
                        && syncedRepoIds.Contains(p.RepositoryId)
                        && !tagPinnedIds.Contains(p.RepositoryId))
            .GroupBy(p => (p.RepositoryId, RepoName: p.Repository!.RepositoryName))
            .Select(g => (g.Key.RepoName, ProjectPaths: (IReadOnlyList<string>)g.Select(p => p.ProjectFilePath!).ToList()))
            .ToList();

        var totalCount = repoGroups.Sum(r => r.ProjectPaths.Count);
        if (totalCount == 0)
            return 0;

        ReportRestoreFailures(await RestoreDependenciesAsync(workspaceId, contextId, repoGroups, cancellationToken), setProgress);
        return totalCount;
    }

    /// <summary>Shows the failed restores as the operation's progress text; the job terminal and the log already have them.</summary>
    private static void ReportRestoreFailures(IReadOnlyList<PackageRestoreOutcome> outcomes, Action<string> setProgress)
    {
        var warnings = outcomes.Where(o => !o.Success).Select(o => o.Warning).ToList();
        if (warnings.Count > 0)
            setProgress(string.Join('\n', warnings));
    }

    private async Task<bool> UsesPackageRestoreAsync(int workspaceId, CancellationToken cancellationToken)
        => (await _capabilitiesResolver.GetAsync(workspaceId, cancellationToken)).UsesPackageRestore;
}

/// <summary>
/// What one repository's <c>dotnet restore</c> reported. <see cref="Reason"/> is the tail of the Worker's error text.
/// </summary>
public sealed record PackageRestoreOutcome(string RepositoryName, bool Success, string? Reason)
{
    private const int MaxReasonLength = 400;

    /// <summary>The line shown to the user for a failed restore.</summary>
    public string Warning => $"{WarningPrefix}{RepositoryName}: {Reason ?? "no reason reported"}";

    private const string WarningPrefix = "Package restore failed for ";

    /// <summary>True when a reported repository error is a restore warning rather than a failed push.</summary>
    public static bool IsWarning(string message) => message.StartsWith(WarningPrefix, StringComparison.Ordinal);

    public static PackageRestoreOutcome Failed(string repositoryName, string? reason)
        => new(repositoryName, false, Tail(reason));

    /// <summary>
    /// Reads the Worker's answer. A command that failed in transit and a restore that ran and reported
    /// <c>success: false</c> are both failures; a response with no body is taken at the command's own word.
    /// </summary>
    public static PackageRestoreOutcome FromResponse(string repositoryName, WorkerCommandResponse response)
    {
        if (!response.Success)
            return Failed(repositoryName, response.Error);
        var result = WorkerResponseJson.DeserializeWorkerResponse<RestoreResult>(response.Data);
        return result is { Success: false }
            ? Failed(repositoryName, result.ErrorMessage)
            : new PackageRestoreOutcome(repositoryName, true, null);
    }

    /// <summary>Writes the warning to the running job's terminal, when the call runs inside a job.</summary>
    public void WriteToJobTerminal()
        => GrayMoon.App.Services.Jobs.TerminalSinkContext.Current?.Append("restore", WorkerCommandStreamKind.Stderr, Warning);

    private static string? Tail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.Trim();
        return trimmed.Length <= MaxReasonLength ? trimmed : "..." + trimmed[^MaxReasonLength..];
    }

    private sealed class RestoreResult
    {
        public bool Success { get; set; } = true;
        public string? ErrorMessage { get; set; }
    }
}
