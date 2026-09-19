using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;
using GrayMoon.Common.FileVersions;
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
            var expectedValues = item.ExpectedValues ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(repositoryName) || string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(pattern))
                continue;
            var repoPath = Path.Combine(workspacePath, repositoryName);
            var fullFilePath = Path.Combine(repoPath, filePath.Replace('/', Path.DirectorySeparatorChar));
            var fileName = Path.GetFileName(fullFilePath);
            var patternEntries = FileVersionTokenParser.ParsePatternLines(pattern);
            if (patternEntries.Count == 0)
                continue;
            var expectedTokenCount = patternEntries.Count(e => expectedValues.ContainsKey(e.Token.TokenKey));
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
                foreach (var entry in patternEntries)
                {
                    var prefix = entry.Prefix;
                    var suffix = entry.Suffix;
                    var tokenKey = entry.Token.TokenKey;
                    if (!trimmedLine.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (suffix.Length > 0 && (trimmedLine.Length < prefix.Length + suffix.Length || !trimmedLine.EndsWith(suffix, StringComparison.Ordinal)))
                        continue;
                    var valueEnd = suffix.Length > 0 ? trimmedLine.Length - suffix.Length : trimmedLine.Length;
                    var currentValue = trimmedLine[prefix.Length..valueEnd];
                    totalMatchedLines++;
                    matchedTokens.Add(tokenKey);
                    if (expectedValues.TryGetValue(tokenKey, out var expectedValue))
                    {
                        var match = currentValue == expectedValue;
                        logger.LogInformation(
                            "CheckFileVersions {FilePath} token {Token}: current={Current} expected={Expected} match={Match}",
                            filePath, tokenKey, currentValue, expectedValue, match);
                        if (!match)
                        {
                            outOfDateLines.Add(new CheckFileVersionsOutOfDateLine
                            {
                                TokenName = tokenKey,
                                CurrentValue = currentValue,
                                ExpectedValue = expectedValue
                            });
                        }
                    }
                    break;
                }
            }
            var notMatchedTokens = patternEntries
                .Select(e => e.Token.TokenKey)
                .Where(r => !matchedTokens.Contains(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var token in notMatchedTokens)
            {
                logger.LogInformation(
                    "CheckFileVersions {FilePath} token {Token}: NOT FOUND in file (not counted as out-of-date)",
                    filePath, token);
            }
            results.Add(new CheckFileVersionsResult
            {
                RepositoryName = repositoryName,
                FilePath = filePath,
                FileName = fileName,
                TotalMatchedLines = totalMatchedLines,
                ExpectedTokenCount = expectedTokenCount,
                OutOfDateLines = outOfDateLines,
                NotMatchedTokens = notMatchedTokens
            });
        }
        return new CheckFileVersionsResponse { Files = results };
    }
    private static (string LeadingWhitespace, int ContentStart) GetLeadingWhitespace(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        return (line[..i], i);
    }
}
