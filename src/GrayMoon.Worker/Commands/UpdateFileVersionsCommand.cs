using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Common.FileVersions;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Services;
namespace GrayMoon.Worker.Commands;
public sealed class UpdateFileVersionsCommand() : ICommandHandler<UpdateFileVersionsRequest, UpdateFileVersionsResponse>
{
    public async Task<UpdateFileVersionsResponse> ExecuteAsync(UpdateFileVersionsRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var repositoryName = request.RepositoryName ?? throw new ArgumentException("repositoryName required");
        var filePath = request.FilePath ?? throw new ArgumentException("filePath required");
        var versionPattern = request.VersionPattern;
        var tokenValues = request.TokenValues ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(versionPattern))
            return new UpdateFileVersionsResponse { UpdatedCount = 0 };
        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);
        // The App is not a trusted source of paths: a rooted or ".." path must never reach the file write below.
        var validation = GitRepositoryPathValidator.Validate(repoPath, filePath);
        if (!validation.IsValid || validation.FullPath is null)
            return new UpdateFileVersionsResponse { UpdatedCount = 0, ErrorMessage = $"{validation.ErrorMessage ?? "Invalid path."} Skipped: {filePath}" };
        var fullFilePath = validation.FullPath;
        // The validator compares text; a junction or symlink inside the repository could still carry the write out.
        if (RepositoryPathContainment.GetEscapeReason(repoPath, fullFilePath) is { } escape)
            return new UpdateFileVersionsResponse { UpdatedCount = 0, ErrorMessage = $"{escape} Skipped: {filePath}" };
        if (!File.Exists(fullFilePath))
            return new UpdateFileVersionsResponse { UpdatedCount = 0, ErrorMessage = $"File not found: {filePath}" };
        var patternEntries = FileVersionTokenParser.ParsePatternLines(versionPattern);
        if (patternEntries.Count == 0)
            return new UpdateFileVersionsResponse { UpdatedCount = 0 };
        // Never rewrite a binary (or huge) file as text lines - that would corrupt it.
        if (TextFileGuard.GetRejectionReason(fullFilePath) is { } rejection)
            return new UpdateFileVersionsResponse { UpdatedCount = 0, ErrorMessage = $"{rejection} Skipped: {filePath}" };
        var fileLines = await File.ReadAllLinesAsync(fullFilePath, cancellationToken);
        var updatedCount = 0;
        var modified = false;
        for (var i = 0; i < fileLines.Length; i++)
        {
            var line = fileLines[i];
            var (leadingWhitespace, contentStart) = GetLeadingWhitespace(line);
            var trimmedLine = contentStart >= line.Length ? "" : line[contentStart..];
            foreach (var entry in patternEntries)
            {
                var prefix = entry.Prefix;
                var suffix = entry.Suffix;
                var tokenKey = entry.Token.TokenKey;
                if (!trimmedLine.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!tokenValues.TryGetValue(tokenKey, out var value)) continue;
                if (suffix.Length > 0 && (trimmedLine.Length < prefix.Length + suffix.Length || !trimmedLine.EndsWith(suffix, StringComparison.Ordinal)))
                    continue;
                var newLine = leadingWhitespace + prefix + value + suffix;
                if (newLine != line)
                {
                    fileLines[i] = newLine;
                    updatedCount++;
                    modified = true;
                }
                break;
            }
        }
        if (modified)
            await File.WriteAllLinesAsync(fullFilePath, fileLines, cancellationToken);
        return new UpdateFileVersionsResponse { UpdatedCount = updatedCount };
    }
    private static (string LeadingWhitespace, int ContentStart) GetLeadingWhitespace(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        return (line[..i], i);
    }
}
