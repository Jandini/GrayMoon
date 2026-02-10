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
            var checksTask = GetCheckRunsAndSuiteStateForRefAsync(connector, owner, repo, @ref, cancellationToken);
            await Task.WhenAll(statusTask, checksTask).ConfigureAwait(false);

            var combinedStatus = await statusTask;
            var (checkRuns, suiteState) = await checksTask;

            var fromStatus = GetStateFromCombinedStatus(combinedStatus);
            var fromChecks = GetStateFromCheckRuns(checkRuns);
            var fromSuites = suiteState ?? BuildStatus.None;

            var merged = MergeBuildState(MergeBuildState(fromStatus, fromChecks), fromSuites);
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

    /// <summary>GitHub returns combined state (success/failure/pending). Prefer top-level state; if empty, derive from statuses so we don't show yellow when all are success.</summary>
    private static BuildStatus GetStateFromCombinedStatus(GitHubCombinedStatusResponse? response)
    {
        if (response == null)
            return BuildStatus.None;

        var state = response.State?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(state))
        {
            return state switch
            {
                "failure" or "error" => BuildStatus.Failure,
                "pending" => BuildStatus.Pending,
                "success" => BuildStatus.Success,
                _ => BuildStatus.None
            };
        }

        if (response.Statuses.Count == 0)
            return BuildStatus.None;

        var statuses = response.Statuses;
        var anyPending = statuses.Any(s => string.Equals(s.State, "pending", StringComparison.OrdinalIgnoreCase));
        if (anyPending)
            return BuildStatus.Pending;
        var anyFailure = statuses.Any(s => string.Equals(s.State, "failure", StringComparison.OrdinalIgnoreCase) || string.Equals(s.State, "error", StringComparison.OrdinalIgnoreCase));
        if (anyFailure)
            return BuildStatus.Failure;
        var anySuccess = statuses.Any(s => string.Equals(s.State, "success", StringComparison.OrdinalIgnoreCase));
        return anySuccess ? BuildStatus.Success : BuildStatus.None;
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

    /// <summary>Only runs that are actually running count as pending. "requested"/"waiting" = not started; GitHub shows green when completed checks passed.</summary>
    private static bool IsCheckRunPending(string? status)
    {
        var s = status?.Trim().ToLowerInvariant();
        return s is "queued" or "in_progress";
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

    /// <summary>Fetches all check runs for ref via check-suites (official CI-agnostic path: list suites for ref, then list runs per suite). Also returns suite-level state when suites exist but have no runs yet.</summary>
    private async Task<(GitHubCheckRunsResponse? Runs, BuildStatus? SuiteState)> GetCheckRunsAndSuiteStateForRefAsync(Connector connector, string owner, string repo, string @ref, CancellationToken cancellationToken)
    {
        var suites = await gitHubService.GetCheckSuitesAsync(connector, owner, repo, @ref, cancellationToken).ConfigureAwait(false);
        if (suites == null || suites.CheckSuites.Count == 0)
            return (null, null);

        var suiteState = GetStateFromCheckSuites(suites);
        var allRuns = new List<GitHubCheckRunDto>();
        foreach (var suite in suites.CheckSuites)
        {
            var runs = await gitHubService.GetCheckRunsForSuiteAsync(connector, owner, repo, suite.Id, cancellationToken).ConfigureAwait(false);
            if (runs?.CheckRuns.Count > 0)
                allRuns.AddRange(runs.CheckRuns);
        }

        var runsResponse = allRuns.Count == 0 ? null : new GitHubCheckRunsResponse { TotalCount = allRuns.Count, CheckRuns = allRuns };
        return (runsResponse, suiteState);
    }

    /// <summary>Only "in_progress" and "queued" mean actually running. "requested"/"waiting" mean not started yet - GitHub still shows green if completed checks passed.</summary>
    private static BuildStatus? GetStateFromCheckSuites(GitHubCheckSuitesResponse suites)
    {
        foreach (var suite in suites.CheckSuites)
        {
            var status = suite.Status?.Trim().ToLowerInvariant();
            if (status is "queued" or "in_progress")
                return BuildStatus.Pending;
            var conclusion = suite.Conclusion?.Trim().ToLowerInvariant();
            if (conclusion is "failure" or "cancelled" or "timed_out" or "action_required")
                return BuildStatus.Failure;
        }
        foreach (var suite in suites.CheckSuites)
        {
            var conclusion = suite.Conclusion?.Trim().ToLowerInvariant();
            if (conclusion == "success")
                return BuildStatus.Success;
        }
        return suites.CheckSuites.Count > 0 ? BuildStatus.None : null;
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
