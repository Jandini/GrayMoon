using GrayMoon.App.Models;

namespace GrayMoon.App.Services;

/// <summary>Fetches CI branch build status from GitHub (commit statuses + check runs) and returns a merged result.</summary>
public class BranchBuildStatusService(GitHubService gitHubService, ILogger<BranchBuildStatusService> logger)
{
    /// <summary>Gets the combined build status for a branch ref. Returns None if no CI is reported or on API error (e.g. 404).</summary>
    public async Task<(BuildStatus Status, string? Tooltip)> GetBuildStatusAsync(
        Connector connector,
        string owner,
        string repo,
        string @ref,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(@ref))
            return (BuildStatus.None, null);

        try
        {
            var statusTask = gitHubService.GetCombinedCommitStatusAsync(connector, owner, repo, @ref, cancellationToken);
            var checkRunsTask = gitHubService.GetCheckRunsAsync(connector, owner, repo, @ref, cancellationToken);
            await Task.WhenAll(statusTask, checkRunsTask).ConfigureAwait(false);

            var combinedStatus = await statusTask;
            var checkRuns = await checkRunsTask;

            var fromStatus = GetStateFromCombinedStatus(combinedStatus);
            var fromChecks = GetStateFromCheckRuns(checkRuns);

            var merged = MergeBuildState(fromStatus, fromChecks);
            var tooltip = BuildTooltip(merged, combinedStatus, checkRuns);
            return (merged, tooltip);
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "GitHub API error fetching build status for {Owner}/{Repo}@{Ref}", owner, repo, @ref);
            return (BuildStatus.None, "CI status unavailable");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error fetching build status for {Owner}/{Repo}@{Ref}", owner, repo, @ref);
            return (BuildStatus.None, "CI status unavailable");
        }
    }

    private static BuildStatus GetStateFromCombinedStatus(GitHubCombinedStatusResponse? response)
    {
        if (response == null || response.Statuses.Count == 0)
            return BuildStatus.None;

        var state = response.State?.Trim().ToLowerInvariant();
        return state switch
        {
            "failure" or "error" => BuildStatus.Failure,
            "pending" => BuildStatus.Pending,
            "success" => BuildStatus.Success,
            _ => BuildStatus.None
        };
    }

    private static BuildStatus GetStateFromCheckRuns(GitHubCheckRunsResponse? response)
    {
        if (response == null || response.CheckRuns.Count == 0)
            return BuildStatus.None;

        var runs = response.CheckRuns;
        var anyPending = runs.Any(r => IsCheckRunPending(r.Status));
        if (anyPending)
            return BuildStatus.Pending;

        var anyFailure = runs.Any(r => IsConclusionFailure(r.Conclusion));
        if (anyFailure)
            return BuildStatus.Failure;

        var anySuccess = runs.Any(r => string.Equals(r.Conclusion, "success", StringComparison.OrdinalIgnoreCase));
        if (anySuccess || runs.Count > 0)
            return BuildStatus.Success;

        return BuildStatus.None;
    }

    private static bool IsCheckRunPending(string? status)
    {
        var s = status?.Trim().ToLowerInvariant();
        return s is "queued" or "in_progress" or "pending" or "waiting" or "requested";
    }

    private static bool IsConclusionFailure(string? conclusion)
    {
        var c = conclusion?.Trim().ToLowerInvariant();
        return c is "failure" or "cancelled" or "timed_out" or "action_required";
    }

    private static BuildStatus MergeBuildState(BuildStatus fromStatus, BuildStatus fromChecks)
    {
        if (fromStatus == BuildStatus.Pending || fromChecks == BuildStatus.Pending)
            return BuildStatus.Pending;
        if (fromStatus == BuildStatus.Failure || fromChecks == BuildStatus.Failure)
            return BuildStatus.Failure;
        if (fromStatus == BuildStatus.Success || fromChecks == BuildStatus.Success)
            return BuildStatus.Success;
        return BuildStatus.None;
    }

    private static string? BuildTooltip(BuildStatus status, GitHubCombinedStatusResponse? combinedStatus, GitHubCheckRunsResponse? checkRuns)
    {
        return status switch
        {
            BuildStatus.Success => "Build passing",
            BuildStatus.Failure => "Build failed",
            BuildStatus.Pending => "Build in progress",
            _ => "No CI status reported to GitHub for this branch"
        };
    }
}
