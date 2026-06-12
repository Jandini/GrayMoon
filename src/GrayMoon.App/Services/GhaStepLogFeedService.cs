namespace GrayMoon.App.Services;

public sealed class GhaStepLogFeedService(
    GitHubService gitHubService,
    ILogger<GhaStepLogFeedService> logger)
{
    public async Task<GhaStepLogUpdate> PollStepLogsAsync(
        GhaWorkflowLiveFeedState feedState,
        GhaStepLogFeedState logState,
        CancellationToken cancellationToken)
    {
        var connector = feedState.CachedConnector;
        var jobId = feedState.CurrentInProgressJobId;
        if (connector == null || jobId == null)
            return GhaStepLogUpdate.Empty;

        try
        {
            var logText = await gitHubService.GetJobLogsAsync(
                connector, feedState.Owner, feedState.RepositoryName, jobId.Value, cancellationToken);

            if (logText == null)
            {
                // API returned non-200 — surface a single diagnostic line the first time
                if (logState.LastStepName != "__api_error__")
                {
                    logState.LastStepName = "__api_error__";
                    logState.LastLineIndex = 0;
                    return new GhaStepLogUpdate("Log · API error", ["Log not available — check server log for HTTP status"], true);
                }
                return GhaStepLogUpdate.Empty;
            }

            if (logText.Length == 0)
            {
                if (logState.LastStepName != "__empty__")
                {
                    logState.LastStepName = "__empty__";
                    logState.LastLineIndex = 0;
                    return new GhaStepLogUpdate("Log · No output", ["Log file returned empty — no step output yet"], true);
                }
                return GhaStepLogUpdate.Empty;
            }

            var (stepName, lines) = ParseCurrentStepLines(logText);

            if (stepName == null)
            {
                var preview = logText.Length > 300 ? logText[..300] : logText;
                logger.LogWarning("GHA step log: no ##[group] markers in {Length}-char log for job {JobId}. First 300 chars: [{Preview}]", logText.Length, jobId, preview);
                if (logState.LastStepName != "__no_groups__")
                {
                    logState.LastStepName = "__no_groups__";
                    logState.LastLineIndex = 0;
                    return new GhaStepLogUpdate("Log · Parsing", [$"Log returned {logText.Length} chars but no ##[group] markers found"], true);
                }
                return GhaStepLogUpdate.Empty;
            }

            var stepChanged = !string.Equals(stepName, logState.LastStepName, StringComparison.Ordinal);
            if (stepChanged)
            {
                logState.LastStepName = stepName;
                logState.LastLineIndex = 0;
            }

            var newLines = lines.Skip(logState.LastLineIndex).ToList();
            logState.LastLineIndex = lines.Count;

            return new GhaStepLogUpdate(StepName: stepName, NewLines: newLines, StepChanged: stepChanged);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "GHA step log poll failed for job {JobId}", jobId);
            return GhaStepLogUpdate.Empty;
        }
    }

    public async Task<string[]> PollStepLogTailAsync(
        GhaWorkflowLiveFeedState feedState,
        int tailLines,
        CancellationToken cancellationToken)
    {
        var connector = feedState.CachedConnector;
        var jobId = feedState.CurrentInProgressJobId;
        if (connector == null || jobId == null)
            return [];

        try
        {
            var logText = await gitHubService.GetJobLogsAsync(
                connector, feedState.Owner, feedState.RepositoryName, jobId.Value, cancellationToken);

            if (string.IsNullOrEmpty(logText))
                return [];

            var (_, lines) = ParseCurrentStepLines(logText);
            return lines.TakeLast(tailLines).ToArray();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "GHA step log tail poll failed for job {JobId}", jobId);
            return [];
        }
    }

    private static (string? stepName, IReadOnlyList<string> lines) ParseCurrentStepLines(string logText)
    {
        var rawLines = logText.Split('\n');
        string? currentName = null;
        var currentLines = new List<string>();
        string? lastClosedName = null;
        List<string> lastClosedLines = [];
        var inGroup = false;

        foreach (var raw in rawLines)
        {
            var content = StripTimestamp(raw);

            if (content.StartsWith("##[group]", StringComparison.Ordinal))
            {
                currentName = content["##[group]".Length..].TrimEnd('\r');
                currentLines = [];
                inGroup = true;
            }
            else if (content.StartsWith("::group::", StringComparison.Ordinal))
            {
                currentName = content["::group::".Length..].TrimEnd('\r');
                currentLines = [];
                inGroup = true;
            }
            else if (content.StartsWith("##[endgroup]", StringComparison.Ordinal)
                     || content.StartsWith("::endgroup::", StringComparison.Ordinal))
            {
                if (inGroup && currentName != null)
                {
                    lastClosedName = currentName;
                    lastClosedLines = new List<string>(currentLines);
                }
                inGroup = false;
            }
            else if (inGroup)
            {
                var stripped = content.TrimEnd('\r');
                if (!string.IsNullOrEmpty(stripped))
                    currentLines.Add(stripped);
            }
        }

        // Prefer the open group — this is the currently running step
        if (inGroup && currentName != null)
            return (currentName, currentLines);

        // Fall back to most recently completed step
        if (lastClosedName != null)
            return (lastClosedName, lastClosedLines);

        return (null, []);
    }

    private static string StripTimestamp(string line)
    {
        // Lines start with "2024-01-15T12:00:00.0000000Z " — 28-char timestamp + space
        // Index 27 = 'Z', index 28 = ' ', content starts at index 29
        if (line.Length > 28 && line[27] == 'Z' && line[28] == ' ')
            return line.Substring(29);
        // Fallback: skip to first space
        var spaceIdx = line.IndexOf(' ');
        return spaceIdx >= 0 ? line.Substring(spaceIdx + 1) : line;
    }
}

public sealed class GhaStepLogFeedState
{
    public string? LastStepName { get; set; }
    public int LastLineIndex { get; set; }
}

public sealed record GhaStepLogUpdate(
    string? StepName,
    IReadOnlyList<string> NewLines,
    bool StepChanged)
{
    public static readonly GhaStepLogUpdate Empty = new(null, [], false);
}
