using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Agent.Commands;

public sealed class CheckFileVersionsCommand(IGitService git, ILogger<CheckFileVersionsCommand> logger) : ICommandHandler<CheckFileVersionsRequest, CheckFileVersionsResponse>
{
    public async Task<CheckFileVersionsResponse> ExecuteAsync(CheckFileVersionsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var items = request.Files;
        if (items == null || items.Count == 0)
            return new CheckFileVersionsResponse { Files = [] };

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var results = new List<CheckFileVersionsResult>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var repositoryName = item.RepositoryName;
            var filePath = item.FilePath;
            var pattern = item.Pattern;
            var expectedVersions = item.ExpectedVersions ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(repositoryName) || string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(pattern))
                continue;

            var repoPath = Path.Combine(workspacePath, repositoryName);
            var fullFilePath = Path.Combine(repoPath, filePath.Replace('/', Path.DirectorySeparatorChar));
            var fileName = Path.GetFileName(fullFilePath);

            var patternEntries = ParsePatternLines(pattern);
            if (patternEntries.Count == 0)
                continue;

            var expectedTokenCount = patternEntries.Count(e => expectedVersions.ContainsKey(e.RepoName));

            if (!File.Exists(fullFilePath))
            {
                results.Add(new CheckFileVersionsResult
                {
                    RepositoryName = repositoryName,
                    FilePath = filePath,
                    FileName = fileName,
                    TotalMatchedLines = 0,
                    ExpectedTokenCount = expectedTokenCount,
                    FileMissing = true,
                    OutOfDateLines = []
                });
                continue;
            }

            var fileLines = await File.ReadAllLinesAsync(fullFilePath, cancellationToken);
            var totalMatchedLines = 0;
            var outOfDateLines = new List<CheckFileVersionsOutOfDateLine>();
            var matchedTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in fileLines)
            {
                var (_, contentStart) = GetLeadingWhitespace(line);
                var trimmedLine = contentStart >= line.Length ? "" : line[contentStart..];

                foreach (var (prefix, repoName, suffix) in patternEntries)
                {
                    if (!trimmedLine.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (suffix.Length > 0 && (trimmedLine.Length < prefix.Length + suffix.Length || !trimmedLine.EndsWith(suffix, StringComparison.Ordinal)))
                        continue;

                    var valueEnd = suffix.Length > 0 ? trimmedLine.Length - suffix.Length : trimmedLine.Length;
                    var currentValue = trimmedLine[prefix.Length..valueEnd];
                    totalMatchedLines++;
                    matchedTokens.Add(repoName);

                    if (expectedVersions.TryGetValue(repoName, out var expectedValue))
                    {
                        var match = currentValue == expectedValue;
                        logger.LogInformation(
                            "CheckFileVersions {FilePath} token {Token}: current={Current} expected={Expected} match={Match}",
                            filePath, repoName, currentValue, expectedValue, match);
                        if (!match)
                        {
                            outOfDateLines.Add(new CheckFileVersionsOutOfDateLine
                            {
                                TokenName = repoName,
                                CurrentValue = currentValue,
                                ExpectedValue = expectedValue
                            });
                        }
                    }
                    break;
                }
            }

            foreach (var missing in BuildMissingTokenLines(patternEntries, expectedVersions, matchedTokens))
            {
                if (!outOfDateLines.Any(o => string.Equals(o.TokenName, missing.TokenName, StringComparison.OrdinalIgnoreCase)))
                {
                    logger.LogInformation(
                        "CheckFileVersions {FilePath} token {Token}: NOT FOUND in file (expected={Expected})",
                        filePath, missing.TokenName, missing.ExpectedValue);
                    outOfDateLines.Add(missing);
                }
            }

            results.Add(new CheckFileVersionsResult
            {
                RepositoryName = repositoryName,
                FilePath = filePath,
                FileName = fileName,
                TotalMatchedLines = totalMatchedLines,
                ExpectedTokenCount = expectedTokenCount,
                OutOfDateLines = outOfDateLines
            });
        }

        return new CheckFileVersionsResponse { Files = results };
    }

    private static List<CheckFileVersionsOutOfDateLine> BuildMissingTokenLines(
        IReadOnlyList<(string Prefix, string RepoName, string Suffix)> patternEntries,
        IReadOnlyDictionary<string, string> expectedVersions,
        IReadOnlySet<string>? matchedTokens)
    {
        var missing = new List<CheckFileVersionsOutOfDateLine>();
        foreach (var (_, repoName, _) in patternEntries)
        {
            if (!expectedVersions.TryGetValue(repoName, out var expectedValue))
                continue;
            if (matchedTokens != null && matchedTokens.Contains(repoName))
                continue;

            missing.Add(new CheckFileVersionsOutOfDateLine
            {
                TokenName = repoName,
                CurrentValue = "",
                ExpectedValue = expectedValue
            });
        }

        return missing;
    }

    private static List<(string Prefix, string RepoName, string Suffix)> ParsePatternLines(string pattern)
    {
        var result = new List<(string, string, string)>();
        foreach (var raw in pattern.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;

            var start = line.IndexOf('{');
            var end = line.IndexOf('}', start >= 0 ? start : 0);
            if (start < 1 || end <= start) continue;

            var prefix = line[..start];
            var repoName = line[(start + 1)..end];
            var suffix = end + 1 < line.Length ? line[(end + 1)..] : "";
            if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(repoName)) continue;

            result.Add((prefix, repoName, suffix));
        }
        return result;
    }

    private static (string LeadingWhitespace, int ContentStart) GetLeadingWhitespace(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        return (line[..i], i);
    }
}
