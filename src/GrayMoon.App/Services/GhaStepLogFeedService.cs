namespace GrayMoon.App.Services;

public sealed class GhaStepLogFeedService(
    GitHubService gitHubService,
    ILogger<GhaStepLogFeedService> logger)
{
    public async Task<string[]> PollStepLogNewLinesAsync(
        GhaWorkflowLiveFeedState feedState,
        int lastLineCount,
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

            var allLines = logText
                .Split('\n')
                .Select(l => StripTimestamp(l).TrimEnd('\r'))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();

            return allLines.Length > lastLineCount ? allLines[lastLineCount..] : [];
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "GHA step log poll failed for job {JobId}", jobId);
            return [];
        }
    }

    private static string StripTimestamp(string line)
    {
        // Lines start with "2024-01-15T12:00:00.0000000Z " — 28-char timestamp + space
        if (line.Length > 28 && line[27] == 'Z' && line[28] == ' ')
            return line.Substring(29);
        var spaceIdx = line.IndexOf(' ');
        return spaceIdx >= 0 ? line.Substring(spaceIdx + 1) : line;
    }
}
