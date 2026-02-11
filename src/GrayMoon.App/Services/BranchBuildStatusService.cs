using GrayMoon.App.Models;

namespace GrayMoon.App.Services;

/// <summary>Fetches CI branch build status from GitHub check suites and returns the result for the current branch.</summary>
public class BranchBuildStatusService(GitHubService gitHubService, ILogger<BranchBuildStatusService> logger)
{
    /// <summary>Gets the combined build status for a branch ref using check suites. Returns None if no CI is reported or on API error (e.g. 404). Only uses suites for the current branch (head_branch).</summary>
    public async Task<(BuildStatus Status, string StatusText, string? Conclusion, string? Tooltip)> GetBuildStatusAsync(
        Connector connector,
        string owner,
        string repo,
        string @ref,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(@ref))
            return (BuildStatus.None, string.Empty, null, null);

        try
        {
            // Resolve branch name to SHA if needed (ref might be a branch name)
            var refToUse = @ref;
            if (!IsSha(@ref))
            {
                var sha = await gitHubService.GetCommitShaAsync(connector, owner, repo, @ref, cancellationToken);
                if (!string.IsNullOrWhiteSpace(sha))
                {
                    refToUse = sha;
                    logger.LogDebug("Resolved branch {Branch} to SHA {Sha} for {Owner}/{Repo}", @ref, sha, owner, repo);
                }
            }

            // Get check suites for the ref
            var suites = await gitHubService.GetCheckSuitesAsync(connector, owner, repo, refToUse, cancellationToken);
            if (suites == null || suites.CheckSuites.Count == 0)
                return (BuildStatus.None, string.Empty, null, "No CI status reported to GitHub for this branch");

            // Filter suites to only those matching the current branch (head_branch)
            var branchSuites = suites.CheckSuites
                .Where(s => string.Equals(s.HeadBranch, @ref, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (branchSuites.Count == 0)
                return (BuildStatus.None, string.Empty, null, "No CI status reported to GitHub for this branch");

            // Get status from suites using status/conclusion combination like actions page
            var (finalStatus, statusText, conclusion) = GetStateFromCheckSuitesForBranch(branchSuites);
            var tooltip = BuildTooltip(finalStatus);
            return (finalStatus, statusText, conclusion, tooltip);
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "GitHub API error fetching build status for {Owner}/{Repo}@{Ref}", owner, repo, @ref);
            return (BuildStatus.None, string.Empty, null, "CI status unavailable");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error fetching build status for {Owner}/{Repo}@{Ref}", owner, repo, @ref);
            return (BuildStatus.None, string.Empty, null, "CI status unavailable");
        }
    }

    /// <summary>Checks if a string looks like a SHA hash (40 hex characters).</summary>
    private static bool IsSha(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 40)
            return false;
        
        return value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }

    /// <summary>Gets build status from check suites for a branch. If multiple suites exist, always prefer the one with a conclusion. Uses conclusion when available (completed/interrupted/etc.), otherwise uses status. Same logic as actions page.</summary>
    private static (BuildStatus Status, string StatusText, string? Conclusion) GetStateFromCheckSuitesForBranch(List<GitHubCheckSuiteDto> suites)
    {
        if (suites.Count == 0)
            return (BuildStatus.None, string.Empty, null);

        // If only one suite, use it as-is
        if (suites.Count == 1)
        {
            var suite = suites[0];
            var status = suite.Status?.Trim() ?? string.Empty;
            var conclusion = suite.Conclusion?.Trim();
            return GetStatusFromSuite(status, conclusion);
        }

        // Multiple suites: prefer ones with conclusions
        var suitesWithConclusion = suites
            .Where(s => !string.IsNullOrWhiteSpace(s.Conclusion))
            .ToList();

        if (suitesWithConclusion.Count > 0)
        {
            // If multiple suites have conclusions, prefer the worst one (failure > success)
            var failureSuite = suitesWithConclusion.FirstOrDefault(s =>
            {
                var conclusionLower = s.Conclusion?.Trim().ToLowerInvariant();
                return conclusionLower is "failure" or "cancelled" or "timed_out" or "action_required";
            });
            if (failureSuite != null)
            {
                var status = failureSuite.Status?.Trim() ?? string.Empty;
                var conclusion = failureSuite.Conclusion?.Trim();
                return GetStatusFromSuite(status, conclusion);
            }

            // Otherwise use the first suite with conclusion (likely success)
            var suiteWithConclusion = suitesWithConclusion[0];
            var statusText = suiteWithConclusion.Status?.Trim() ?? string.Empty;
            var conclusionText = suiteWithConclusion.Conclusion?.Trim();
            return GetStatusFromSuite(statusText, conclusionText);
        }

        // No suites with conclusions, use the first suite's status
        var firstSuite = suites[0];
        var firstStatus = firstSuite.Status?.Trim() ?? string.Empty;
        return GetStatusFromSuite(firstStatus, null);
    }

    /// <summary>Converts suite status and conclusion to BuildStatus enum and returns the appropriate values.</summary>
    private static (BuildStatus Status, string StatusText, string? Conclusion) GetStatusFromSuite(string status, string? conclusion)
    {
        // Use conclusion if available (completed/interrupted/etc.)
        if (!string.IsNullOrWhiteSpace(conclusion))
        {
            var conclusionLower = conclusion.Trim().ToLowerInvariant();
            return conclusionLower switch
            {
                "success" => (BuildStatus.Success, status, conclusion),
                "failure" or "cancelled" or "timed_out" or "action_required" => (BuildStatus.Failure, status, conclusion),
                _ => (BuildStatus.None, status, conclusion)
            };
        }

        // Otherwise use status
        var statusLower = status.ToLowerInvariant();
        return statusLower switch
        {
            "queued" or "in_progress" => (BuildStatus.Pending, status, null),
            _ => (BuildStatus.None, status, null)
        };
    }

    private static string? BuildTooltip(BuildStatus status)
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
