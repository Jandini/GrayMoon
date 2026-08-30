namespace GrayMoon.App.Services.GitHub;

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
