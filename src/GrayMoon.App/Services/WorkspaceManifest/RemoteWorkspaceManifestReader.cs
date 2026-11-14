using System.Net.Http;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.GitHub;
using GrayMoon.Application.Workspaces;
using GrayMoon.Common.Manifest;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.WorkspaceManifest;

/// <summary>Result of reading <c>graymoon.json</c> from an imported repository without cloning it.</summary>
/// <param name="Found">False when the repository has no such file (not an error).</param>
/// <param name="Content">The file text when <paramref name="Found"/> is true.</param>
/// <param name="Error">Set when the file could not be read at all (repository, connector or network problem).</param>
public sealed record RemoteManifestRead(bool Found, string? Content, string? Error);

/// <summary>Reads the Workspace definition of an imported repository through its connector (no Worker, no clone).</summary>
public interface IRemoteWorkspaceManifestReader
{
    /// <summary>Reads the definition at the tip of <paramref name="branchName"/>, or of the default branch when it is null.</summary>
    Task<RemoteManifestRead> ReadAsync(int repositoryId, string? branchName, CancellationToken cancellationToken = default);

    Task<RemoteManifestRead> ReadAsync(int repositoryId, CancellationToken cancellationToken = default)
        => ReadAsync(repositoryId, null, cancellationToken);
}

/// <summary>
/// Reads <c>graymoon.json</c> (at a chosen branch, else the default branch) and lists remote branches with the GitHub
/// API of the repository's connector.
/// </summary>
public sealed class GitHubRemoteWorkspaceManifestReader(
    IDbContextFactory<AppDbContext> dbContextFactory,
    GitHubService gitHubService,
    IWorkspaceManifestFile manifestFile,
    ILogger<GitHubRemoteWorkspaceManifestReader> logger) : IRemoteWorkspaceManifestReader, IRemoteRepositoryBranchReader
{
    public async Task<RemoteManifestRead> ReadAsync(int repositoryId, string? branchName, CancellationToken cancellationToken = default)
    {
        var (target, error) = await ResolveAsync(repositoryId, cancellationToken);
        if (target is null)
            return new RemoteManifestRead(false, null, error);

        try
        {
            var content = await gitHubService.GetRepositoryFileUtf8TextAsync(
                target.Connector, target.Owner, target.Name, manifestFile.FileName, cancellationToken, branchName);
            return content is null
                ? new RemoteManifestRead(false, null, null)
                : new RemoteManifestRead(true, content, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not read the Workspace definition of repository {RepositoryId} from GitHub", repositoryId);
            return new RemoteManifestRead(false, null, $"Could not read {manifestFile.FileName} from GitHub: {Detail(ex)}");
        }
    }

    public async Task<RemoteBranchList> ListAsync(int repositoryId, CancellationToken cancellationToken = default)
    {
        var (target, error) = await ResolveAsync(repositoryId, cancellationToken);
        if (target is null)
            return RemoteBranchList.Failed(error!);

        try
        {
            var list = await gitHubService.GetRepositoryBranchesAsync(target.Connector, target.Owner, target.Name, cancellationToken);
            return new RemoteBranchList(list.DefaultBranch, OrderBranches(list.DefaultBranch, list.Branches), list.Truncated, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not list the branches of repository {RepositoryId} from GitHub", repositoryId);
            return RemoteBranchList.Failed($"Could not list branches from GitHub: {Detail(ex)}");
        }
    }

    /// <summary>The default branch first (when GitHub returned it), then the others sorted ignoring case; duplicates dropped.</summary>
    public static IReadOnlyList<string> OrderBranches(string? defaultBranch, IEnumerable<string> branches)
    {
        var distinct = branches.Distinct(StringComparer.Ordinal).ToList();
        var ordered = new List<string>(distinct.Count);
        if (defaultBranch is not null && distinct.Remove(defaultBranch))
            ordered.Add(defaultBranch);
        ordered.AddRange(distinct.OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ThenBy(b => b, StringComparer.Ordinal));
        return ordered;
    }

    private static string Detail(Exception ex) =>
        ex is HttpRequestException http ? GitHubApiErrorHelper.FormatFriendlyGitHubHttpError(http) : ex.Message;

    private sealed record Target(Connector Connector, string Owner, string Name);

    private async Task<(Target? Target, string? Error)> ResolveAsync(int repositoryId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var repository = await db.Repositories
            .AsNoTracking()
            .Include(r => r.Connector)
            .FirstOrDefaultAsync(r => r.RepositoryId == repositoryId, cancellationToken);
        if (repository is null)
            return (null, "Repository not found.");
        if (repository.Connector is not { ConnectorType: ConnectorType.GitHub } connector)
            return (null, "The repository is not imported through a GitHub connector.");

        if (!RepositoryUrlHelper.TryParseGitHubOwnerRepo(repository.CloneUrl, out var owner, out var name)
            || string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
        {
            owner = repository.OrgName;
            name = repository.RepositoryName;
        }

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
            return (null, "The repository owner could not be determined.");

        return (new Target(connector, owner, name), null);
    }
}
