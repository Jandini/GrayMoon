using GrayMoon.App.Data;
using GrayMoon.App.Models;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.GitHub;

/// <summary>Creates GitHub pull requests for one or many workspace repositories.</summary>
public interface IPullRequestService
{
    Task<CreatePullRequestResult> CreatePullRequestAsync(
        CreatePullRequestRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CreatePullRequestResult>> CreatePullRequestsAsync(
        IReadOnlyList<CreatePullRequestRequest> requests,
        IProgress<CreatePullRequestProgress>? progress,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetCollaboratorLoginsAsync(int repositoryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetTeamSlugsAsync(int repositoryId, CancellationToken cancellationToken);

    /// <summary>
    /// Union of collaborator logins and team slugs for <paramref name="repositoryIds"/>, for the New Pull Request reviewer list.
    /// Repositories are resolved in one query, each distinct GitHub repository is asked at most once per call, and answers are
    /// cached for a few minutes so reopening the dialog does not repeat the GitHub requests. Never throws for a GitHub failure;
    /// that repository simply contributes nothing.
    /// </summary>
    Task<PullRequestReviewerCandidates> GetReviewerCandidatesAsync(IReadOnlyCollection<int> repositoryIds, CancellationToken cancellationToken);
}

/// <summary>Reviewer options offered by the New Pull Request dialog.</summary>
public sealed record PullRequestReviewerCandidates(IReadOnlyList<string> Users, IReadOnlyList<string> Teams)
{
    public static PullRequestReviewerCandidates Empty { get; } = new([], []);
}

public sealed class PullRequestService(
    AppDbContext dbContext,
    GitHubService gitHubService,
    IOptions<WorkspaceOptions> workspaceOptions,
    IMemoryCache memoryCache,
    ILogger<PullRequestService> logger) : IPullRequestService
{
    /// <summary>How long reviewer lists are reused; collaborators and teams rarely change while a dialog is reopened.</summary>
    internal static readonly TimeSpan ReviewerCacheDuration = TimeSpan.FromMinutes(5);

    private int MaxConcurrency => Math.Max(1, workspaceOptions.Value.MaxParallelOperations);

    public async Task<PullRequestReviewerCandidates> GetReviewerCandidatesAsync(IReadOnlyCollection<int> repositoryIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repositoryIds);
        if (repositoryIds.Count == 0)
            return PullRequestReviewerCandidates.Empty;

        var started = Stopwatch.GetTimestamp();
        var ids = repositoryIds.Distinct().ToList();
        var repos = await dbContext.Repositories
            .AsNoTracking()
            .Include(r => r.Connector)
            .Where(r => ids.Contains(r.RepositoryId))
            .ToListAsync(cancellationToken);

        // Several workspace repositories can point at the same GitHub repository (same connector, owner and name).
        var targets = new Dictionary<string, (Connector Connector, string Owner, string Name)>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in repos)
        {
            if (repo.Connector is not { } connector || connector.ConnectorType != ConnectorType.GitHub)
                continue;
            if (!RepositoryUrlHelper.TryParseGitHubOwnerRepo(repo.CloneUrl, out var owner, out var name) || owner == null || name == null)
                continue;
            targets.TryAdd($"{connector.ConnectorId}:{owner}/{name}", (connector, owner, name));
        }

        var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var teams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cacheHits = 0;
        using var gate = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        await Task.WhenAll(targets.Select(async kvp =>
        {
            var (key, target) = (kvp.Key, kvp.Value);
            var userTask = GetCachedAsync($"pr-reviewers:users:{key}", async ct =>
            {
                var collaborators = await gitHubService.GetCollaboratorsAsync(target.Connector, target.Owner, target.Name, ct);
                return collaborators.Select(c => c.Login).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            });
            var teamTask = GetCachedAsync($"pr-reviewers:teams:{key}", async ct =>
            {
                var repoTeams = await gitHubService.GetTeamsAsync(target.Connector, target.Owner, target.Name, ct);
                return repoTeams.Select(t => t.Slug).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            });
            var (userList, teamList) = (await userTask, await teamTask);
            lock (users)
            {
                users.UnionWith(userList);
                teams.UnionWith(teamList);
            }
        }));

        logger.LogInformation(
            "New PR reviewers: {RepositoryCount} repositories, {GitHubRepositoryCount} distinct GitHub repositories, {CacheHits} cache hits, {ElapsedMs}ms",
            ids.Count, targets.Count, cacheHits, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return new PullRequestReviewerCandidates(
            users.OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList(),
            teams.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList());

        async Task<IReadOnlyList<string>> GetCachedAsync(string cacheKey, Func<CancellationToken, Task<List<string>>> fetch)
        {
            if (memoryCache.TryGetValue(cacheKey, out IReadOnlyList<string>? cached) && cached is not null)
            {
                Interlocked.Increment(ref cacheHits);
                return cached;
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                var fetched = await fetch(cancellationToken);
                // Only successful answers are cached, so a transient GitHub failure is retried next time.
                memoryCache.Set(cacheKey, (IReadOnlyList<string>)fetched, ReviewerCacheDuration);
                return fetched;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Reviewer lookup failed for {CacheKey}", cacheKey);
                return [];
            }
            finally
            {
                gate.Release();
            }
        }
    }

    public async Task<CreatePullRequestResult> CreatePullRequestAsync(
        CreatePullRequestRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Title))
            return Fail(request, "Title is required.");
        if (string.IsNullOrWhiteSpace(request.Owner))
            return Fail(request, "Repository owner is required.");
        if (string.IsNullOrWhiteSpace(request.RepositoryName))
            return Fail(request, "Repository name is required.");
        if (string.IsNullOrWhiteSpace(request.HeadBranch))
            return Fail(request, "Head branch is required.");
        if (string.IsNullOrWhiteSpace(request.BaseBranch))
            return Fail(request, "Base branch is required.");

        var repo = await dbContext.Repositories
            .AsNoTracking()
            .Include(r => r.Connector)
            .FirstOrDefaultAsync(r => r.RepositoryId == request.RepositoryId, cancellationToken);

        if (repo?.Connector is not { } connector)
            return Fail(request, "GitHub connector not configured for this repository.");

        return await ExecuteCreatePullRequestAsync(request, connector, cancellationToken);
    }

    private async Task<CreatePullRequestResult> ExecuteCreatePullRequestAsync(
        CreatePullRequestRequest request,
        Connector connector,
        CancellationToken cancellationToken)
    {
        if (connector.ConnectorType != ConnectorType.GitHub)
            return Fail(request, "Repository connector is not a GitHub connector.");

        var body = new GitHubCreatePullRequestRequestDto
        {
            Title = request.Title.Trim(),
            Head = request.HeadBranch,
            Base = request.BaseBranch,
            Body = string.IsNullOrWhiteSpace(request.Body) ? null : request.Body,
            Draft = request.IsDraft
        };

        GitHubPullRequestDto created;
        try
        {
            created = await gitHubService.CreatePullRequestAsync(connector, request.Owner, request.RepositoryName, body, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            var friendly = GitHubApiErrorHelper.FormatFriendlyGitHubHttpError(ex);
            logger.LogWarning(ex, "Create PR failed for {Owner}/{Repo} {Head}->{Base}", request.Owner, request.RepositoryName, request.HeadBranch, request.BaseBranch);
            return Fail(request, friendly);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Create PR errored for {Owner}/{Repo} {Head}->{Base}", request.Owner, request.RepositoryName, request.HeadBranch, request.BaseBranch);
            return Fail(request, ex.Message);
        }

        string? reviewerWarning = null;
        if ((request.Reviewers.Count > 0 || request.TeamReviewers.Count > 0) && created.Number > 0)
        {
            try
            {
                await gitHubService.RequestReviewersAsync(
                    connector,
                    request.Owner,
                    request.RepositoryName,
                    created.Number,
                    request.Reviewers,
                    request.TeamReviewers,
                    cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                reviewerWarning = GitHubApiErrorHelper.FormatFriendlyGitHubHttpError(ex);
                logger.LogWarning(ex, "Request reviewers failed for {Owner}/{Repo} PR #{Number}", request.Owner, request.RepositoryName, created.Number);
            }
            catch (Exception ex)
            {
                reviewerWarning = ex.Message;
                logger.LogWarning(ex, "Request reviewers errored for {Owner}/{Repo} PR #{Number}", request.Owner, request.RepositoryName, created.Number);
            }
        }

        logger.LogInformation("Created PR #{Number} for {Owner}/{Repo} {Head}->{Base}",
            created.Number, request.Owner, request.RepositoryName, request.HeadBranch, request.BaseBranch);

        return new CreatePullRequestResult
        {
            RepositoryId = request.RepositoryId,
            RepositoryName = request.RepositoryName,
            Success = true,
            PullRequestNumber = created.Number,
            PullRequestUrl = created.HtmlUrl,
            ReviewerWarning = reviewerWarning
        };
    }

    public async Task<IReadOnlyList<CreatePullRequestResult>> CreatePullRequestsAsync(
        IReadOnlyList<CreatePullRequestRequest> requests,
        IProgress<CreatePullRequestProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var total = requests.Count;
        progress?.Report(new CreatePullRequestProgress { Created = 0, Failed = 0, Total = total });

        var ids = requests.Select(r => r.RepositoryId).Distinct().ToList();
        var repoById = await dbContext.Repositories
            .AsNoTracking()
            .Include(r => r.Connector)
            .Where(r => ids.Contains(r.RepositoryId))
            .ToDictionaryAsync(r => r.RepositoryId, cancellationToken);

        var results = new CreatePullRequestResult[requests.Count];
        var created = 0;
        var failed = 0;

        using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        var tasks = requests.Select(async (request, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                CreatePullRequestResult result;

                if (string.IsNullOrWhiteSpace(request.Title))
                    result = Fail(request, "Title is required.");
                else if (string.IsNullOrWhiteSpace(request.Owner))
                    result = Fail(request, "Repository owner is required.");
                else if (string.IsNullOrWhiteSpace(request.RepositoryName))
                    result = Fail(request, "Repository name is required.");
                else if (string.IsNullOrWhiteSpace(request.HeadBranch))
                    result = Fail(request, "Head branch is required.");
                else if (string.IsNullOrWhiteSpace(request.BaseBranch))
                    result = Fail(request, "Base branch is required.");
                else if (!repoById.TryGetValue(request.RepositoryId, out var repo) || repo?.Connector is not { } connector)
                    result = Fail(request, "GitHub connector not configured for this repository.");
                else
                    result = await ExecuteCreatePullRequestAsync(request, connector, cancellationToken);

                results[index] = result;

                int c, f;
                if (result.Success)
                {
                    c = Interlocked.Increment(ref created);
                    f = Volatile.Read(ref failed);
                }
                else
                {
                    f = Interlocked.Increment(ref failed);
                    c = Volatile.Read(ref created);
                }

                progress?.Report(new CreatePullRequestProgress
                {
                    Created = c,
                    Failed = f,
                    Total = total,
                    CurrentRepositoryName = request.RepositoryName
                });
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    public async Task<IReadOnlyList<string>> GetCollaboratorLoginsAsync(int repositoryId, CancellationToken cancellationToken)
    {
        var ctx = await ResolveRepoContextAsync(repositoryId, cancellationToken);
        if (ctx == null) return Array.Empty<string>();

        try
        {
            var collaborators = await gitHubService.GetCollaboratorsAsync(ctx.Value.Connector, ctx.Value.Owner, ctx.Value.Name, cancellationToken);
            return collaborators
                .Where(c => !string.IsNullOrWhiteSpace(c.Login))
                .Select(c => c.Login)
                .OrderBy(login => login, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "GetCollaboratorLogins failed. RepositoryId={RepositoryId}", repositoryId);
            return Array.Empty<string>();
        }
    }

    public async Task<IReadOnlyList<string>> GetTeamSlugsAsync(int repositoryId, CancellationToken cancellationToken)
    {
        var ctx = await ResolveRepoContextAsync(repositoryId, cancellationToken);
        if (ctx == null) return Array.Empty<string>();

        try
        {
            var teams = await gitHubService.GetTeamsAsync(ctx.Value.Connector, ctx.Value.Owner, ctx.Value.Name, cancellationToken);
            return teams
                .Where(t => !string.IsNullOrWhiteSpace(t.Slug))
                .Select(t => t.Slug)
                .OrderBy(slug => slug, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "GetTeamSlugs failed. RepositoryId={RepositoryId}", repositoryId);
            return Array.Empty<string>();
        }
    }

    private async Task<(Connector Connector, string Owner, string Name)?> ResolveRepoContextAsync(int repositoryId, CancellationToken cancellationToken)
    {
        var repo = await dbContext.Repositories
            .AsNoTracking()
            .Include(r => r.Connector)
            .FirstOrDefaultAsync(r => r.RepositoryId == repositoryId, cancellationToken);

        if (repo?.Connector is not { } connector || connector.ConnectorType != ConnectorType.GitHub)
            return null;

        if (!RepositoryUrlHelper.TryParseGitHubOwnerRepo(repo.CloneUrl, out var owner, out var name) || owner == null || name == null)
            return null;

        return (connector, owner, name);
    }

    private static CreatePullRequestResult Fail(CreatePullRequestRequest request, string message) => new()
    {
        RepositoryId = request.RepositoryId,
        RepositoryName = request.RepositoryName,
        Success = false,
        ErrorMessage = message
    };
}
