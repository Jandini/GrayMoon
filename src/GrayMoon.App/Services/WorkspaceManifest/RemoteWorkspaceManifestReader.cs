using System.Net.Http;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.GitHub;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.WorkspaceManifest;

/// <summary>Result of reading <c>.graymoon.json</c> from an imported repository without cloning it.</summary>
/// <param name="Found">False when the repository has no such file (not an error).</param>
/// <param name="Content">The file text when <paramref name="Found"/> is true.</param>
/// <param name="Error">Set when the file could not be read at all (repository, connector or network problem).</param>
public sealed record RemoteManifestRead(bool Found, string? Content, string? Error);

/// <summary>Reads the Workspace definition of an imported repository through its connector (no Worker, no clone).</summary>
public interface IRemoteWorkspaceManifestReader
{
    Task<RemoteManifestRead> ReadAsync(int repositoryId, CancellationToken cancellationToken = default);
}

/// <summary>Reads <c>.graymoon.json</c> from the default branch with the GitHub contents API of the repository's connector.</summary>
public sealed class GitHubRemoteWorkspaceManifestReader(
    IDbContextFactory<AppDbContext> dbContextFactory,
    GitHubService gitHubService,
    ILogger<GitHubRemoteWorkspaceManifestReader> logger) : IRemoteWorkspaceManifestReader
{
    public async Task<RemoteManifestRead> ReadAsync(int repositoryId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var repository = await db.Repositories
            .AsNoTracking()
            .Include(r => r.Connector)
            .FirstOrDefaultAsync(r => r.RepositoryId == repositoryId, cancellationToken);
        if (repository is null)
            return new RemoteManifestRead(false, null, "Repository not found.");
        if (repository.Connector is not { ConnectorType: ConnectorType.GitHub } connector)
            return new RemoteManifestRead(false, null, "The repository is not imported through a GitHub connector.");

        if (!RepositoryUrlHelper.TryParseGitHubOwnerRepo(repository.CloneUrl, out var owner, out var name)
            || string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
        {
            owner = repository.OrgName;
            name = repository.RepositoryName;
        }

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
            return new RemoteManifestRead(false, null, "The repository owner could not be determined.");

        try
        {
            var content = await gitHubService.GetRepositoryFileUtf8TextAsync(
                connector, owner, name, WorkspaceRepositoryFileAccess.ManifestFilePath, cancellationToken);
            return content is null
                ? new RemoteManifestRead(false, null, null)
                : new RemoteManifestRead(true, content, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not read the Workspace definition of repository {RepositoryId} from GitHub", repositoryId);
            var detail = ex is HttpRequestException http ? GitHubApiErrorHelper.FormatFriendlyGitHubHttpError(http) : ex.Message;
            return new RemoteManifestRead(false, null, $"Could not read .graymoon.json from GitHub: {detail}");
        }
    }
}
