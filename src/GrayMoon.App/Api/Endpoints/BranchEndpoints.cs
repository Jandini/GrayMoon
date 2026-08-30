namespace GrayMoon.App.Api.Endpoints;

public static class BranchEndpoints
{
    public static IEndpointRouteBuilder MapBranchEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/branches/get", GetBranches);
        routes.MapPost("/api/branches/refresh", RefreshBranches);
        routes.MapPost("/api/branches/checkout", CheckoutBranch);
        routes.MapPost("/api/branches/sync-to-default", SyncToDefaultBranch);
        routes.MapPost("/api/branches/common", GetCommonBranches);
        routes.MapPost("/api/branches/exists-in-workspace", BranchExistsInWorkspace);
        routes.MapPost("/api/branches/create", CreateBranch);
        routes.MapPost("/api/branches/set-upstream", SetUpstreamBranch);
        routes.MapPost("/api/branches/delete", DeleteBranch);
        routes.MapPost("/api/branches/update-from-default", UpdateBranchFromDefault);
        return routes;
    }

    private static async Task<IResult> BranchExistsInWorkspace(
        BranchExistsInWorkspaceApiRequest? body,
        IWorkspaceBranchOperations operations,
        CancellationToken cancellationToken)
    {
        if (body == null)
            return Results.BadRequest("Request body is required.");
        if (body.WorkspaceId <= 0 || string.IsNullOrWhiteSpace(body.BranchName))
            return Results.BadRequest("workspaceId and branchName are required.");

        return (await operations.CountReposWithLocalBranchAsync(body.WorkspaceId, body.BranchName, cancellationToken)).ToHttpResult();
    }

    private static async Task<IResult> GetBranches(
        GetBranchesApiRequest? body,
        IWorkspaceBranchOperations operations,
        CancellationToken cancellationToken)
    {
        if (body == null)
            return Results.BadRequest("Request body is required.");
        if (body.WorkspaceId <= 0 || body.RepositoryId <= 0)
            return Results.BadRequest("workspaceId and repositoryId are required.");

        return (await operations.GetBranchesAsync(body.WorkspaceId, body.RepositoryId, cancellationToken)).ToHttpResult();
    }

    private static async Task<IResult> CheckoutBranch(
        CheckoutBranchApiRequest? body,
        IWorkspaceBranchOperations operations,
        CancellationToken cancellationToken)
    {
        if (body == null)
            return Results.BadRequest("Request body is required.");
        if (body.WorkspaceId <= 0 || body.RepositoryId <= 0 || string.IsNullOrWhiteSpace(body.BranchName))
            return Results.BadRequest("workspaceId, repositoryId, and branchName are required.");

        return (await operations.CheckoutAsync(
            body.WorkspaceId,
            body.RepositoryId,
            body.BranchName,
            body.IsTag,
            cancellationToken)).ToHttpResult();
    }

    private static async Task<IResult> SyncToDefaultBranch(
        SyncToDefaultBranchApiRequest? body,
        IWorkspaceBranchOperations operations,
        CancellationToken cancellationToken)
    {
        if (body == null)
            return Results.BadRequest("Request body is required.");
        if (body.WorkspaceId <= 0 || body.RepositoryId <= 0 || string.IsNullOrWhiteSpace(body.CurrentBranchName))
            return Results.BadRequest("workspaceId, repositoryId, and currentBranchName are required.");

        return (await operations.SyncToDefaultAsync(
            body.WorkspaceId,
            body.RepositoryId,
            body.CurrentBranchName,
            body.DeleteRemoteBranch,
            body.AllowForceDeleteLocalBranch,
            cancellationToken)).ToHttpResult();
    }

    private static async Task<IResult> RefreshBranches(
        RefreshBranchesApiRequest? body,
        IWorkspaceBranchOperations operations,
        CancellationToken cancellationToken)
    {
        if (body == null)
            return Results.BadRequest("Request body is required.");
        if (body.WorkspaceId <= 0 || body.RepositoryId <= 0)
            return Results.BadRequest("workspaceId and repositoryId are required.");

        return (await operations.RefreshBranchesAsync(body.WorkspaceId, body.RepositoryId, cancellationToken)).ToHttpResult();
    }

    private static async Task<IResult> CreateBranch(
        CreateBranchApiRequest? body,
        IWorkspaceBranchOperations operations,
        CancellationToken cancellationToken)
    {
        if (body == null)
            return Results.BadRequest("Request body is required.");
        if (body.WorkspaceId <= 0 || body.RepositoryId <= 0 || string.IsNullOrWhiteSpace(body.NewBranchName))
            return Results.BadRequest("workspaceId, repositoryId, and newBranchName are required.");

        return (await operations.CreateBranchAsync(
            body.WorkspaceId,
            body.RepositoryId,
            body.NewBranchName,
            body.BaseBranch,
            cancellationToken)).ToHttpResult();
    }

    private static async Task<IResult> SetUpstreamBranch(
        SetUpstreamBranchApiRequest? body,
        IWorkspaceBranchOperations operations,
        CancellationToken cancellationToken)
    {
        if (body == null)
            return Results.BadRequest("Request body is required.");
        if (body.WorkspaceId <= 0 || body.RepositoryId <= 0 || string.IsNullOrWhiteSpace(body.BranchName))
            return Results.BadRequest("workspaceId, repositoryId, and branchName are required.");

        return (await operations.SetUpstreamAsync(
            body.WorkspaceId,
            body.RepositoryId,
            body.BranchName,
            cancellationToken)).ToHttpResult();
    }

    private static async Task<IResult> DeleteBranch(
        DeleteBranchApiRequest? body,
        IWorkspaceBranchOperations operations,
        CancellationToken cancellationToken)
    {
        if (body == null)
            return Results.BadRequest("Request body is required.");
        if (body.WorkspaceId <= 0 || body.RepositoryId <= 0 || string.IsNullOrWhiteSpace(body.BranchName))
            return Results.BadRequest("workspaceId, repositoryId, and branchName are required.");

        return (await operations.DeleteBranchAsync(
            body.WorkspaceId,
            body.RepositoryId,
            body.BranchName,
            body.IsRemote,
            body.Force,
            cancellationToken)).ToHttpResult();
    }

    private static async Task<IResult> UpdateBranchFromDefault(
        UpdateBranchFromDefaultApiRequest? body,
        IWorkspaceBranchOperations operations,
        CancellationToken cancellationToken)
    {
        if (body == null)
            return Results.BadRequest("Request body is required.");
        if (body.WorkspaceId <= 0 || body.RepositoryId <= 0)
            return Results.BadRequest("workspaceId and repositoryId are required.");

        return (await operations.UpdateBranchFromDefaultAsync(body.WorkspaceId, body.RepositoryId, cancellationToken)).ToHttpResult();
    }

    private static async Task<IResult> GetCommonBranches(
        CommonBranchesApiRequest? body,
        IWorkspaceBranchOperations operations,
        CancellationToken cancellationToken)
    {
        if (body == null)
            return Results.BadRequest("Request body is required.");
        if (body.WorkspaceId <= 0)
            return Results.BadRequest("workspaceId is required.");

        return (await operations.GetCommonBranchesAsync(body.WorkspaceId, cancellationToken)).ToHttpResult();
    }
}

public sealed class RefreshBranchesApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
}

public sealed class GetBranchesApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
}

public sealed class CheckoutBranchApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
    public string? BranchName { get; set; }
    /// <summary>When true, <see cref="BranchName"/> is treated as a tag name and the agent dispatches a CheckoutTag command (detached HEAD). Defaults to false for backward compatibility.</summary>
    public bool IsTag { get; set; }
}

/// <summary>API response for POST /api/branches/checkout (serialized to camelCase).</summary>
public sealed class CheckoutBranchApiResult
{
    public bool Success { get; set; }
    public string? CurrentBranch { get; set; }
    public string? ErrorMessage { get; set; }

    public CheckoutBranchApiResult(bool success, string? errorMessage)
    {
        Success = success;
        ErrorMessage = errorMessage;
    }
}

public sealed class SyncToDefaultBranchApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
    public string? CurrentBranchName { get; set; }
    public bool DeleteRemoteBranch { get; set; }
    public bool AllowForceDeleteLocalBranch { get; set; } = true;
}

public sealed class CommonBranchesApiRequest
{
    public int WorkspaceId { get; set; }
}

public sealed class BranchExistsInWorkspaceApiRequest
{
    public int WorkspaceId { get; set; }
    public string? BranchName { get; set; }
}

public sealed class CreateBranchApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
    public string? NewBranchName { get; set; }
    public string? BaseBranch { get; set; }
}

public sealed class SetUpstreamBranchApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
    public string? BranchName { get; set; }
}

public sealed class DeleteBranchApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
    public string? BranchName { get; set; }
    public bool IsRemote { get; set; }
    /// <summary>When true, local delete uses git branch -D (after user confirmed not-fully-merged warning).</summary>
    public bool Force { get; set; }
}

public sealed class UpdateBranchFromDefaultApiRequest
{
    public int WorkspaceId { get; set; }
    public int RepositoryId { get; set; }
}

/// <summary>Mirrors UpdateBranchFromDefaultResponse from the Agent for JSON deserialization on the App side.</summary>
public sealed class UpdateBranchFromDefaultResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("success")]
    public bool Success { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("hasConflicts")]
    public bool HasConflicts { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("conflictFiles")]
    public IReadOnlyList<string>? ConflictFiles { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("outgoingCommits")]
    public int? OutgoingCommits { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("incomingCommits")]
    public int? IncomingCommits { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("defaultBranchBehind")]
    public int? DefaultBranchBehind { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("defaultBranchAhead")]
    public int? DefaultBranchAhead { get; set; }
}
