using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class UpdateFileVersionsCommand(IGitService git) : ICommandHandler<UpdateFileVersionsRequest, UpdateFileVersionsResponse>
{
    public async Task<UpdateFileVersionsResponse> ExecuteAsync(UpdateFileVersionsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var filePath = request.FilePath ?? throw new ArgumentException("filePath required");
        var versionPattern = request.VersionPattern;
        var repoVersions = request.RepoVersions ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(versionPattern))
            return new UpdateFileVersionsResponse { UpdatedCount = 0 };

        var workspacePath = git.GetWorkspacePath(workspaceName);
        var repoPath = Path.Combine(workspacePath, repositoryName);
        var fullFilePath = Path.Combine(repoPath, filePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(fullFilePath))
            return new UpdateFileVersionsResponse { UpdatedCount = 0, ErrorMessage = $"File not found: {filePath}" };

        // Parse pattern lines: each is PREFIX={reponame} — extract (prefix, repoName) tuples
        var patternEntries = ParsePatternLines(versionPattern);
        if (patternEntries.Count == 0)
            return new UpdateFileVersionsResponse { UpdatedCount = 0 };

        var fileLines = await File.ReadAllLinesAsync(fullFilePath, cancellationToken);
        var updatedCount = 0;
        var modified = false;

        for (var i = 0; i < fileLines.Length; i++)
        {
            var line = fileLines[i];
            foreach (var (prefix, repoName) in patternEntries)
            {
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!repoVersions.TryGetValue(repoName, out var version)) continue;

                var newLine = prefix + version;
                if (newLine != line)
                {
                    fileLines[i] = newLine;
                    updatedCount++;
                    modified = true;
                }
                break; // only one pattern can match per line
            }
        }

        if (modified)
            await File.WriteAllLinesAsync(fullFilePath, fileLines, cancellationToken);

        return new UpdateFileVersionsResponse { UpdatedCount = updatedCount };
    }

    /// <summary>
    /// Parses pattern text into (prefix, repoName) tuples.
    /// Each non-empty line must contain exactly one {token}; the prefix is everything up to and
    /// including the character before '{'. Example: "KEY={repo}" → prefix="KEY=", repoName="repo".
    /// </summary>
    private static List<(string Prefix, string RepoName)> ParsePatternLines(string pattern)
    {
        var result = new List<(string, string)>();
        foreach (var raw in pattern.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;

            var start = line.IndexOf('{');
            var end = line.IndexOf('}', start >= 0 ? start : 0);
            if (start < 1 || end <= start) continue; // need at least one char before '{'

            var prefix = line[..start];          // e.g. "KEY="
            var repoName = line[(start + 1)..end]; // e.g. "repo"
            if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(repoName)) continue;

            result.Add((prefix, repoName));
        }
        return result;
    }
}
