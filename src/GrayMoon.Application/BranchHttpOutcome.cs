namespace GrayMoon.Application;

/// <summary>
/// HTTP-mappable result for branch operations. Endpoints map this via an App adapter;
/// in-process callers inspect <see cref="StatusCode"/> and <see cref="Body"/>.
/// </summary>
public sealed class BranchHttpOutcome
{
    public int StatusCode { get; init; }
    public object? Body { get; init; }
    public string? ProblemTitle { get; init; }

    public bool IsSuccessStatus => StatusCode is >= 200 and < 300;

    public string? ErrorText => ProblemTitle ?? Body as string;

    public static BranchHttpOutcome Ok(object? body) => new() { StatusCode = 200, Body = body };

    public static BranchHttpOutcome BadRequest(string message) => new() { StatusCode = 400, Body = message };

    public static BranchHttpOutcome NotFound(string message) => new() { StatusCode = 404, Body = message };

    public static BranchHttpOutcome Problem(string title, int status) => new()
    {
        StatusCode = status,
        ProblemTitle = title
    };
}

/// <summary>Persisted branch/tag lists for a workspace repository (POST /api/branches/get).</summary>
public sealed class WorkspaceBranchesSnapshot
{
    public List<string> LocalBranches { get; init; } = [];
    public List<string> RemoteBranches { get; init; } = [];
    public string? CurrentBranch { get; init; }
    public string? DefaultBranch { get; init; }
    public List<string> Tags { get; init; } = [];
    public string? CurrentTag { get; init; }
}

public sealed class BranchExistsCount
{
    public int Count { get; init; }
}

/// <summary>JSON body for POST /api/branches/update-from-default.</summary>
public sealed class UpdateBranchFromDefaultHttpBody
{
    public bool Success { get; init; }
    public bool HasConflicts { get; init; }
    public IReadOnlyList<string> ConflictFiles { get; init; } = [];
}
