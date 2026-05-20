using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

/// <summary>
/// Shared polling logic for GitHub Actions run jobs/steps so multiple UI surfaces can stream the same live feed.
/// </summary>
public sealed class GhaWorkflowLiveFeedService(
    GitHubService gitHubService,
    ConnectorRepository connectorRepository,
    ILogger<GhaWorkflowLiveFeedService> logger)
{
    public const int PollIntervalActiveMs = 1_200;
    public const int PollIntervalWaitingJobsMs = 2_000;
    public const int PollIntervalIdleMs = 4_000;

    public async Task<GhaWorkflowLiveFeedUpdate> PollOnceAsync(
        GhaWorkflowLiveFeedState state,
        CancellationToken cancellationToken)
    {
        try
        {
            var connector = await ResolveConnectorAsync(state, cancellationToken);
            if (connector == null)
            {
                return new GhaWorkflowLiveFeedUpdate(
                    Caption: state.LastCaption,
                    StepProgress: state.LastStepProgress,
                    NewLines: ["Connector not found - cannot load job status."],
                    DelayMs: PollIntervalWaitingJobsMs);
            }

            var response = await gitHubService.GetWorkflowRunJobsAsync(connector, state.Owner, state.RepositoryName, state.RunId, cancellationToken);
            var jobs = response?.Jobs ?? [];

            var caption = BuildCaption(jobs, state.WorkflowDisplayName);
            var stepProgress = BuildStepProgressForCaptionJob(jobs);
            state.LastCaption = caption;
            state.LastStepProgress = stepProgress;

            if (jobs.Count == 0)
            {
                return new GhaWorkflowLiveFeedUpdate(
                    Caption: caption,
                    StepProgress: null,
                    NewLines: ["Waiting for GitHub to assign jobs to this run..."],
                    DelayMs: PollIntervalWaitingJobsMs);
            }

            var newLines = new List<string>();
            if (!state.SnapshottedSteps)
            {
                state.SnapshottedSteps = true;
                foreach (var job in jobs.OrderBy(j => j.Id))
                {
                    foreach (var step in (job.Steps ?? []).OrderBy(s => s.Number))
                        state.StepSignatures[$"{job.Id}:{step.Number}"] = $"{step.Status}|{step.Conclusion}";
                }

                var stepCount = jobs.Sum(j => j.Steps?.Count ?? 0);
                newLines.Add($"Live - {jobs.Count} job(s), {stepCount} step(s) - streaming updates...");
            }
            else
            {
                foreach (var job in jobs.OrderBy(j => j.Id))
                {
                    var steps = job.Steps ?? [];
                    foreach (var step in steps.OrderBy(s => s.Number))
                    {
                        var key = $"{job.Id}:{step.Number}";
                        var sig = $"{step.Status}|{step.Conclusion}";
                        if (state.StepSignatures.TryGetValue(key, out var prev) && prev == sig)
                            continue;

                        state.StepSignatures[key] = sig;
                        newLines.Add(FormatStepTransition(job.Name, step));
                    }
                }
            }

            return new GhaWorkflowLiveFeedUpdate(
                Caption: caption,
                StepProgress: stepProgress,
                NewLines: newLines,
                DelayMs: DeterminePollDelayMs(jobs));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "GHA live feed poll failed for run {RunId}", state.RunId);
            var failureText = ex is HttpRequestException httpEx
                ? GitHubApiErrorHelper.FormatFriendlyGitHubHttpError(httpEx)
                : ex.Message;
            return new GhaWorkflowLiveFeedUpdate(
                Caption: state.LastCaption,
                StepProgress: state.LastStepProgress,
                NewLines: [$"Update failed: {failureText}"],
                DelayMs: PollIntervalWaitingJobsMs);
        }
    }

    private async Task<Connector?> ResolveConnectorAsync(GhaWorkflowLiveFeedState state, CancellationToken cancellationToken)
    {
        if (state.CachedConnector != null
            && string.Equals(state.CachedConnectorName, state.ConnectorName, StringComparison.Ordinal))
        {
            return state.CachedConnector;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var connector = await connectorRepository.GetByNameAsync(state.ConnectorName);
        if (connector != null)
        {
            state.CachedConnector = connector;
            state.CachedConnectorName = state.ConnectorName;
        }

        return connector;
    }

    private static int DeterminePollDelayMs(IReadOnlyList<GitHubWorkflowJobDto> jobs)
    {
        foreach (var job in jobs)
        {
            var js = job.Status ?? "";
            if (js.Equals("in_progress", StringComparison.OrdinalIgnoreCase)
                || js.Equals("queued", StringComparison.OrdinalIgnoreCase)
                || js.Equals("waiting", StringComparison.OrdinalIgnoreCase))
            {
                return PollIntervalActiveMs;
            }

            var steps = job.Steps;
            if (steps == null) continue;
            foreach (var step in steps)
            {
                if (string.Equals(step.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
                    return PollIntervalActiveMs;
            }
        }

        return PollIntervalIdleMs;
    }

    private static string BuildCaption(IReadOnlyList<GitHubWorkflowJobDto> jobs, string? workflowName)
    {
        var wf = string.IsNullOrWhiteSpace(workflowName) ? "Workflow" : workflowName!;
        if (jobs.Count == 0)
            return $"{wf} - Waiting for jobs...";

        var job = GetCaptionJob(jobs)!;
        var steps = job.Steps ?? [];
        var step = steps.FirstOrDefault(s => string.Equals(s.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
                   ?? steps.LastOrDefault(s => string.Equals(s.Status, "completed", StringComparison.OrdinalIgnoreCase));

        var stepLabel = step?.Name;
        if (string.IsNullOrWhiteSpace(stepLabel))
        {
            if (string.Equals(job.Status, "queued", StringComparison.OrdinalIgnoreCase))
                stepLabel = "Queued for runner...";
            else if (string.Equals(job.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
                stepLabel = "Running...";
            else
                stepLabel = job.Status;
        }

        return $"{wf} - {job.Name} > {stepLabel}";
    }

    private static GitHubWorkflowJobDto? GetCaptionJob(IReadOnlyList<GitHubWorkflowJobDto> jobs)
    {
        if (jobs.Count == 0) return null;
        return jobs.FirstOrDefault(j => string.Equals(j.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
               ?? jobs.FirstOrDefault(j => string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase))
               ?? jobs.FirstOrDefault(j => string.Equals(j.Status, "waiting", StringComparison.OrdinalIgnoreCase))
               ?? jobs[0];
    }

    private static string? BuildStepProgressForCaptionJob(IReadOnlyList<GitHubWorkflowJobDto> jobs)
    {
        var job = GetCaptionJob(jobs);
        if (job == null) return null;

        var ordered = (job.Steps ?? []).OrderBy(s => s.Number).ToList();
        var y = ordered.Count;
        if (y == 0)
            return null;

        var inProgressIdx = ordered.FindIndex(s => string.Equals(s.Status, "in_progress", StringComparison.OrdinalIgnoreCase));
        if (inProgressIdx >= 0)
            return $"Step {inProgressIdx + 1} of {y}";

        for (var i = 0; i < ordered.Count; i++)
        {
            if (!string.Equals(ordered[i].Status, "completed", StringComparison.OrdinalIgnoreCase))
                return $"Step {i + 1} of {y}";
        }

        return $"Step {y} of {y}";
    }

    private static string FormatStepTransition(string jobName, GitHubWorkflowJobStepDto step)
    {
        if (string.Equals(step.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            var c = step.Conclusion ?? "";
            if (string.Equals(c, "success", StringComparison.OrdinalIgnoreCase))
                return $"OK {jobName} > {step.Name}";
            if (string.Equals(c, "skipped", StringComparison.OrdinalIgnoreCase))
                return $"SKIP {jobName} > {step.Name} (skipped)";
            if (string.Equals(c, "failure", StringComparison.OrdinalIgnoreCase) || string.Equals(c, "cancelled", StringComparison.OrdinalIgnoreCase))
                return $"FAIL {jobName} > {step.Name} ({c})";
            return $"INFO {jobName} > {step.Name} - {c}";
        }

        if (string.Equals(step.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
            return $"RUN {jobName} > {step.Name}...";

        return $"WAIT {jobName} > {step.Name} - {step.Status}";
    }
}

public sealed class GhaWorkflowLiveFeedState
{
    public required string ConnectorName { get; init; }
    public required string Owner { get; init; }
    public required string RepositoryName { get; init; }
    public required long RunId { get; init; }
    public string? WorkflowDisplayName { get; init; }

    internal Connector? CachedConnector { get; set; }
    internal string? CachedConnectorName { get; set; }
    internal bool SnapshottedSteps { get; set; }
    internal Dictionary<string, string> StepSignatures { get; } = new(StringComparer.Ordinal);
    internal string LastCaption { get; set; } = "Workflow - Connecting to GitHub Actions...";
    internal string? LastStepProgress { get; set; }
}

public sealed record GhaWorkflowLiveFeedUpdate(
    string Caption,
    string? StepProgress,
    IReadOnlyList<string> NewLines,
    int DelayMs);