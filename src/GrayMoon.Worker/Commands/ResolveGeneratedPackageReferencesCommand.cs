using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Common.FileVersions;
using GrayMoon.Worker.Services;
namespace GrayMoon.Worker.Commands;
/// <summary>
/// For a configured .csproj version file, resolves which PackageReference (Include name) each version-pattern
/// line refers to by parsing the real .csproj with <see cref="ICsProjFileParser"/> (element-scoped, not line-based),
/// so multiline attributes, attribute ordering, and whitespace variations are handled the same way as normal
/// PackageReference parsing/updating.
/// </summary>
public sealed class ResolveGeneratedPackageReferencesCommand(ICsProjFileParser csProjFileParser)
    : ICommandHandler<ResolveGeneratedPackageReferencesRequest, ResolveGeneratedPackageReferencesResponse>
{
    public async Task<ResolveGeneratedPackageReferencesResponse> ExecuteAsync(ResolveGeneratedPackageReferencesRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var items = request.Files;
        if (items == null || items.Count == 0)
            return new ResolveGeneratedPackageReferencesResponse { Files = [] };
        var workspacePath = WorkerRepositoryPaths.GetWorkspacePath(request.WorkspaceRoot!, workspaceName);
        var results = new List<ResolveGeneratedPackageReferencesFileResult>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repositoryName = item.RepositoryName;
            var filePath = item.FilePath;
            var pattern = item.Pattern;
            if (string.IsNullOrWhiteSpace(repositoryName) || string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(pattern))
                continue;
            var repoPath = WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName);
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
            foreach (var entry in FileVersionTokenParser.ParsePatternLines(pattern))
            {
                // Package references only make sense for GitVersion tokens; branch/commit stamps are skipped.
                if (entry.Token.Kind != FileVersionTokenKind.GitVersion)
                    continue;
                var match = await csProjFileParser.FindPackageReferenceForVersionPatternAsync(fullFilePath, entry.Prefix, entry.Suffix, cancellationToken);
                if (match != null && !string.IsNullOrWhiteSpace(match.Include))
                {
                    fileResult.Packages.Add(new ResolveGeneratedPackageReferencesPackageEntry
                    {
                        RepoNameToken = entry.Token.RepositoryName,
                        PackageName = match.Include,
                        Version = match.Version
                    });
                }
            }
            results.Add(fileResult);
        }
        return new ResolveGeneratedPackageReferencesResponse { Files = results };
    }
}
