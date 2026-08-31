using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

/// <summary>
/// For a configured .csproj version file, resolves which PackageReference (Include name) each version-pattern
/// line refers to by parsing the real .csproj with <see cref="ICsProjFileParser"/> (element-scoped, not line-based),
/// so multiline attributes, attribute ordering, and whitespace variations are handled the same way as normal
/// PackageReference parsing/updating.
/// </summary>
public sealed class ResolveGeneratedPackageReferencesCommand(IGitService git, ICsProjFileParser csProjFileParser)
    : ICommandHandler<ResolveGeneratedPackageReferencesRequest, ResolveGeneratedPackageReferencesResponse>
{
    public async Task<ResolveGeneratedPackageReferencesResponse> ExecuteAsync(ResolveGeneratedPackageReferencesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var items = request.Files;
        if (items == null || items.Count == 0)
            return new ResolveGeneratedPackageReferencesResponse { Files = [] };

        var workspacePath = git.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var results = new List<ResolveGeneratedPackageReferencesFileResult>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var repositoryName = item.RepositoryName;
            var filePath = item.FilePath;
            var pattern = item.Pattern;
            if (string.IsNullOrWhiteSpace(repositoryName) || string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(pattern))
                continue;

            var repoPath = Path.Combine(workspacePath, repositoryName);
            var fullFilePath = Path.Combine(repoPath, filePath.Replace('/', Path.DirectorySeparatorChar));

            var fileResult = new ResolveGeneratedPackageReferencesFileResult
            {
                RepositoryName = repositoryName,
                FilePath = filePath
            };

            if (!File.Exists(fullFilePath))
            {
                results.Add(fileResult);
                continue;
            }

            foreach (var (prefix, repoName, suffix) in ParsePatternLines(pattern))
            {
                var match = await csProjFileParser.FindPackageReferenceForVersionPatternAsync(fullFilePath, prefix, suffix, cancellationToken);
                if (match != null && !string.IsNullOrWhiteSpace(match.Include))
                {
                    fileResult.Packages.Add(new ResolveGeneratedPackageReferencesPackageEntry
                    {
                        RepoNameToken = repoName,
                        PackageName = match.Include,
                        Version = match.Version
                    });
                }
            }

            results.Add(fileResult);
        }

        return new ResolveGeneratedPackageReferencesResponse { Files = results };
    }

    /// <summary>
    /// Parses pattern text into (prefix, repoName, suffix) tuples.
    /// Each non-empty line must contain exactly one {token}; the prefix is everything before '{',
    /// the suffix is everything after '}'. Example: "Version=\"{RepoA}\" />" -> prefix="Version=\"", repoName="RepoA", suffix="\" />".
    /// </summary>
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
}
