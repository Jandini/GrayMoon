using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services;

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
}

public sealed class PullRequestService(
    AppDbContext dbContext,
    GitHubService gitHubService,
    ILogger<PullRequestService> logger) : IPullRequestService
{
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

        var results = new List<CreatePullRequestResult>(requests.Count);
        var created = 0;
        var failed = 0;
        var total = requests.Count;

        progress?.Report(new CreatePullRequestProgress { Created = 0, Failed = 0, Total = total });

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new CreatePullRequestProgress
            {
                Created = created,
                Failed = failed,
                Total = total,
                CurrentRepositoryName = request.RepositoryName
            });

            var result = await CreatePullRequestAsync(request, cancellationToken);
            results.Add(result);

            if (result.Success)
                created++;
            else
                failed++;

            progress?.Report(new CreatePullRequestProgress
            {
                Created = created,
                Failed = failed,
                Total = total,
                CurrentRepositoryName = request.RepositoryName
            });
        }

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

public sealed class CreatePullRequestRequest
{
    public required int RepositoryId { get; init; }
    public required string Owner { get; init; }
    public required string RepositoryName { get; init; }
    public required string HeadBranch { get; init; }
    public required string BaseBranch { get; init; }
    public required string Title { get; init; }
    public string? Body { get; init; }
    public bool IsDraft { get; init; }
    public IReadOnlyList<string> Reviewers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TeamReviewers { get; init; } = Array.Empty<string>();
}

public sealed class CreatePullRequestResult
{
    public required int RepositoryId { get; init; }
    public required string RepositoryName { get; init; }
    public bool Success { get; init; }
    public int? PullRequestNumber { get; init; }
    public string? PullRequestUrl { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ReviewerWarning { get; init; }
}

public sealed class CreatePullRequestProgress
{
    public int Created { get; init; }
    public int Failed { get; init; }
    public int Total { get; init; }
    public string? CurrentRepositoryName { get; init; }
}
