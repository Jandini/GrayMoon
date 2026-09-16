using GrayMoon.App.Models;

namespace GrayMoon.App.Services.GitHub;

public sealed class MergePullRequestRequest
{
    public required int RepositoryId { get; init; }
    public required int PrNumber { get; init; }
    public MergeMethod? Method { get; init; }
    public string? ExpectedHeadSha { get; init; }
}

public sealed class MergePullRequestResult
{
    public required int RepositoryId { get; init; }
    public required int PrNumber { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class MergePullRequestProgress
{
    public int Completed { get; init; }
    public int Failed { get; init; }
    public int Total { get; init; }
    public int? CurrentRepositoryId { get; init; }
    public bool? CurrentSuccess { get; init; }
    public string? CurrentErrorMessage { get; init; }
}
