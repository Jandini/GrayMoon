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

            if (string.IsNullOrEmpty(logText))
                return GhaStepLogUpdate.Empty;

            var (stepName, lines) = ParseCurrentStepLines(logText);

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
            logger.LogDebug(ex, "GHA step log poll failed for job {JobId}", jobId);
            return GhaStepLogUpdate.Empty;
        }
    }

    private static (string? stepName, IReadOnlyList<string> lines) ParseCurrentStepLines(string logText)
    {
        var rawLines = logText.Split('\n');
        string? currentName = null;
        var currentLines = new List<string>();
        var inGroup = false;

        foreach (var raw in rawLines)
        {
            var content = StripTimestamp(raw);

            // GitHub runner writes ##[group] markers for step boundaries (older format);
            // user-created groups within a step use ::group:: (newer format)
            if (content.StartsWith("##[group]", StringComparison.Ordinal))
            {
                currentName = content.Substring("##[group]".Length).TrimEnd('\r');
                currentLines = [];
                inGroup = true;
            }
            else if (content.StartsWith("##[endgroup]", StringComparison.Ordinal)
                     || content.StartsWith("::endgroup::", StringComparison.Ordinal))
            {
                inGroup = false;
            }
            else if (content.StartsWith("::group::", StringComparison.Ordinal))
            {
                currentName = content.Substring("::group::".Length).TrimEnd('\r');
                currentLines = [];
                inGroup = true;
            }
            else if (inGroup)
            {
                var stripped = content.TrimEnd('\r');
                if (!string.IsNullOrEmpty(stripped))
                    currentLines.Add(stripped);
            }
        }

        return inGroup && currentName != null
            ? (currentName, currentLines)
            : (null, []);
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
