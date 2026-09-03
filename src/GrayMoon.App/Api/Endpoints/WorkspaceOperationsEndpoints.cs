using GrayMoon.App.Models;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Queries;
using GrayMoon.Common.Git;

namespace GrayMoon.App.Api.Endpoints;

public static class WorkspaceOperationsEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceOperationsEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/workspaces", ListWorkspaces);
        routes.MapGet("/api/workspaces/{workspaceId:int}", GetWorkspace);

        var group = routes.MapGroup("/api/workspaces/{workspaceId:int}").WithTags("WorkspaceOperations");
        group.MapGet("/repos", ListRepos);
        group.MapGet("/operations", GetRunningOperation);
        group.MapPost("/update", Update);
        group.MapPost("/push", Push);
        group.MapPost("/new-feature", NewFeature);
        group.MapPost("/sync", Sync);
        group.MapPost("/sync-to-default", SyncToDefault);
        group.MapPost("/pull", Pull);
        group.MapPost("/undo-push", UndoPush);
        group.MapPost("/restore-packages", RestorePackages);
        group.MapPost("/pull-requests", CreatePullRequests);
        group.MapPost("/pull-requests/merge", MergePullRequest);
        group.MapGet("/git-changes", GetGitChanges);
        group.MapPost("/git-changes/commit", CommitGitChanges);
        group.MapPost("/git-changes/stage", StageGitChanges);
        group.MapPost("/git-changes/unstage", UnstageGitChanges);
        group.MapPost("/files/update-versions", UpdateFileVersions);
        return routes;
    }

    private static async Task<IResult> ListWorkspaces(IWorkspaceCatalogOperations catalog, CancellationToken cancellationToken)
        => Results.Ok(await catalog.ListAsync(cancellationToken));

    private static async Task<IResult> GetWorkspace(int workspaceId, IWorkspaceCatalogOperations catalog, CancellationToken cancellationToken)
    {
        var item = await catalog.GetAsync(workspaceId, cancellationToken);
        return item == null ? Results.NotFound("Workspace not found.") : Results.Ok(item);
    }

    private static async Task<IResult> ListRepos(
        int workspaceId,
        IWorkspaceCatalogOperations catalog,
        IWorkspaceRepositoryLinkListQueryService query,
        CancellationToken cancellationToken)
    {
        if (await catalog.GetAsync(workspaceId, cancellationToken) == null)
            return Results.NotFound("Workspace not found.");

        return Results.Ok(await query.GetAllSnapshotsAsync(workspaceId, cancellationToken));
    }

    private static IResult GetRunningOperation(int workspaceId, IWorkspaceOperationRunner runner)
    {
        var op = runner.GetRunning(workspaceId);
        if (op == null)
            return Results.Ok(new { running = false });

        return Results.Ok(new
        {
            running = op.State == BackgroundJobState.Running,
            id = op.Id,
            operationKind = op.OperationKind,
            displayMessage = op.DisplayMessage,
            state = op.State.ToString()
        });
    }

    private static Task<IResult> Update(
        int workspaceId,
        UpdateWorkspaceApiRequest? body,
        IWorkspaceUpdateOperations operations,
        IWorkspaceOperationRunner runner,
        CancellationToken cancellationToken)
        => WorkspaceCommandHttp.RunExclusiveAsync(runner, workspaceId, "Updating dependencies...", async (progress, ct) =>
        {
            var result = await operations.UpdateAsync(
                workspaceId,
                ct,
                progress,
                (_, _) => { },
                (_, _) => { },
                repoIdsToUpdate: body?.RepositoryIds?.ToHashSet(),
                commitMessage: body?.CommitMessage,
                includeDepsInCommitMessage: body?.IncludeDepsInCommitMessage ?? true,
                maxLevel: body?.MaxLevel);
            return result.Success
                ? Results.Ok(new { success = true })
                : Results.BadRequest(new { success = false, error = "Update failed." });
        }, cancellationToken);

    private static Task<IResult> Push(
        int workspaceId,
        PushWorkspaceApiRequest? body,
        IWorkspacePushOperations operations,
        IWorkspaceOperationRunner runner,
        CancellationToken cancellationToken)
        => WorkspaceCommandHttp.RunExclusiveAsync(runner, workspaceId, "Preparing push...", async (progress, ct) =>
        {
            var result = body?.RepositoryIds is { Count: > 0 } ids
                ? await operations.PushAsync(
                    workspaceId,
                    ids.ToHashSet(),
                    body.SynchronizedPush,
                    body.RequiredPackageIds?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                        ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    progress,
                    cancellationToken: ct)
                : await operations.PushPendingAsync(workspaceId, body?.SynchronizedPush ?? true, progress, ct);

            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }, cancellationToken);

    private static Task<IResult> NewFeature(
        int workspaceId,
        NewFeatureApiRequest? body,
        IWorkspaceFeatureOperations operations,
        IWorkspacePushOperations pushOperations,
        IWorkspaceOperationRunner runner,
        CancellationToken cancellationToken)
    {
        if (body == null || string.IsNullOrWhiteSpace(body.NewBranchName))
            return Task.FromResult(Results.BadRequest("newBranchName is required."));

        return WorkspaceCommandHttp.RunExclusiveAsync(runner, workspaceId, "Creating branches...", async (progress, ct) =>
        {
            var created = await operations.CreateAsync(
                workspaceId,
                body.NewBranchName.Trim(),
                body.BaseBranch ?? "__default__",
                body.RepositoryIds?.ToHashSet(),
                body.UpdateDependencies,
                body.CommitMessage,
                progress,
                (_, _) => { },
                (_, _) => { },
                ct);

            if (!created.ShouldChainPush(body.PushChanges))
            {
                return created.Success
                    ? Results.Ok(new { success = true, pushed = false })
                    : Results.BadRequest(new { success = false, error = "Update failed." });
            }

            var push = await pushOperations.PushPendingAsync(workspaceId, synchronizedPush: true, progress, ct);
            return push.Success
                ? Results.Ok(new { success = true, pushed = true })
                : Results.BadRequest(push);
        }, cancellationToken);
    }

    private static Task<IResult> Sync(
        int workspaceId,
        SyncWorkspaceApiRequest? body,
        IWorkspaceSyncOperations operations,
        IWorkspaceOperationRunner runner,
        CancellationToken cancellationToken)
        => WorkspaceCommandHttp.RunExclusiveAsync(runner, workspaceId, "Synchronizing...", async (progress, ct) =>
        {
            await operations.SyncAsync(
                workspaceId,
                body?.RepositoryIds,
                skipDependencyLevelPersistence: false,
                ct,
                progress,
                (_, _) => { },
                (_, _) => { });
            return Results.Ok(new { success = true });
        }, cancellationToken);

    private static Task<IResult> SyncToDefault(
        int workspaceId,
        SyncToDefaultApiRequest? body,
        IWorkspaceSyncOperations operations,
        IWorkspaceOperationRunner runner,
        CancellationToken cancellationToken)
    {
        if (body?.RepositoryIds is not { Count: > 0 })
            return Task.FromResult(Results.BadRequest("repositoryIds is required."));

        return WorkspaceCommandHttp.RunExclusiveAsync(runner, workspaceId, "Synchronizing to default branch...", async (progress, ct) =>
        {
            var result = await operations.SyncToDefaultAsync(workspaceId, body.RepositoryIds, progress, ct);
            return result.Completed ? Results.Ok(result) : Results.BadRequest(result);
        }, cancellationToken);
    }

    private static Task<IResult> Pull(
        int workspaceId,
        PullWorkspaceApiRequest? body,
        IWorkspaceSyncOperations operations,
        IWorkspaceOperationRunner runner,
        CancellationToken cancellationToken)
        => WorkspaceCommandHttp.RunExclusiveAsync(runner, workspaceId, "Synchronizing commits...", async (progress, ct) =>
        {
            var result = body?.RepositoryIds is { Count: > 1 } ids
                ? await operations.PullLevelAsync(workspaceId, ids, progress, ct)
                : await operations.PullAsync(workspaceId, body?.RepositoryId ?? body?.RepositoryIds?.FirstOrDefault() ?? 0, progress, ct);

            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }, cancellationToken);

    private static Task<IResult> UndoPush(
        int workspaceId,
        UndoPushApiRequest? body,
        IWorkspaceSyncOperations operations,
        IWorkspaceOperationRunner runner,
        CancellationToken cancellationToken)
        => WorkspaceCommandHttp.RunExclusiveAsync(runner, workspaceId, "Reverting outgoing commits...", async (progress, ct) =>
        {
            var result = await operations.UndoPushAsync(workspaceId, body?.KeepChanges ?? true, progress, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }, cancellationToken);

    private static Task<IResult> RestorePackages(
        int workspaceId,
        IWorkspaceUpdateOperations operations,
        IWorkspaceOperationRunner runner,
        CancellationToken cancellationToken)
        => WorkspaceCommandHttp.RunExclusiveAsync(runner, workspaceId, "Restoring packages...", async (progress, ct) =>
        {
            var count = await operations.RestorePackagesAsync(workspaceId, progress, ct);
            return Results.Ok(new { success = true, restored = count });
        }, cancellationToken);

    private static Task<IResult> CreatePullRequests(
        int workspaceId,
        CreatePullRequestsApiRequest? body,
        IWorkspacePullRequestOperations operations,
        IWorkspaceOperationRunner runner,
        CancellationToken cancellationToken)
    {
        if (body?.Requests is not { Count: > 0 })
            return Task.FromResult(Results.BadRequest("requests is required."));

        return WorkspaceCommandHttp.RunExclusiveAsync(runner, workspaceId, "Creating pull requests...", async (_, ct) =>
        {
            var results = await operations.CreateAsync(body.Requests, progress: null, ct);
            return Results.Ok(results);
        }, cancellationToken);
    }

    private static Task<IResult> MergePullRequest(
        int workspaceId,
        MergePullRequestApiRequest? body,
        IWorkspacePullRequestOperations operations,
        IWorkspaceOperationRunner runner,
        CancellationToken cancellationToken)
    {
        if (body == null || body.RepositoryId <= 0 || body.PullRequestNumber <= 0)
            return Task.FromResult(Results.BadRequest("repositoryId and pullRequestNumber are required."));

        return WorkspaceCommandHttp.RunExclusiveAsync(runner, workspaceId, "Merging pull requests...", async (_, ct) =>
        {
            var result = await operations.MergeAsync(
                workspaceId,
                body.RepositoryId,
                body.PullRequestNumber,
                body.Method,
                body.ExpectedHeadSha,
                ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }, cancellationToken);
    }

    private static async Task<IResult> GetGitChanges(
        int workspaceId,
        IWorkspaceGitChangesOperations operations,
        CancellationToken cancellationToken)
    {
        var view = await operations.GetAsync(workspaceId, cancellationToken);
        return view == null ? Results.NotFound("Workspace not found.") : Results.Ok(view);
    }

    private static Task<IResult> CommitGitChanges(
        int workspaceId,
        GitChangesCommitApiRequest? body,
        IWorkspaceGitChangesOperations operations,
        IWorkspaceOperationRunner runner,
        CancellationToken cancellationToken)
    {
        if (body == null || body.RepositoryId <= 0 || string.IsNullOrWhiteSpace(body.Message))
            return Task.FromResult(Results.BadRequest("repositoryId and message are required."));

        return WorkspaceCommandHttp.RunExclusiveAsync(runner, workspaceId, "Committing...", async (_, ct) =>
        {
            var result = await operations.CommitAsync(workspaceId, body.RepositoryId, body.Message, body.StageAllFirst, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }, cancellationToken);
    }

    private static Task<IResult> StageGitChanges(
        int workspaceId,
        GitChangesPathsApiRequest? body,
        IWorkspaceGitChangesOperations operations,
        IWorkspaceOperationRunner runner,
        CancellationToken cancellationToken)
    {
        if (body == null || body.RepositoryId <= 0)
            return Task.FromResult(Results.BadRequest("repositoryId is required."));

        return WorkspaceCommandHttp.RunExclusiveAsync(runner, workspaceId, "Staging...", async (_, ct) =>
        {
            var result = await operations.StageAsync(workspaceId, body.RepositoryId, body.Scope, body.Paths ?? [], ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }, cancellationToken);
    }

    private static Task<IResult> UnstageGitChanges(
        int workspaceId,
        GitChangesPathsApiRequest? body,
        IWorkspaceGitChangesOperations operations,
        IWorkspaceOperationRunner runner,
        CancellationToken cancellationToken)
    {
        if (body == null || body.RepositoryId <= 0)
            return Task.FromResult(Results.BadRequest("repositoryId is required."));

        return WorkspaceCommandHttp.RunExclusiveAsync(runner, workspaceId, "Unstaging...", async (_, ct) =>
        {
            var result = await operations.UnstageAsync(workspaceId, body.RepositoryId, body.Scope, body.Paths ?? [], ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        }, cancellationToken);
    }

    private static Task<IResult> UpdateFileVersions(
        int workspaceId,
        IWorkspaceFileOperations operations,
        IWorkspaceOperationRunner runner,
        CancellationToken cancellationToken)
        => WorkspaceCommandHttp.RunExclusiveAsync(runner, workspaceId, "Updating file versions...", async (_, ct) =>
        {
            var result = await operations.UpdateVersionsAsync(workspaceId, ct);
            return string.IsNullOrWhiteSpace(result.Error)
                ? Results.Ok(new { success = true, updated = result.Updated, failed = result.Failed })
                : Results.BadRequest(new { success = false, updated = result.Updated, failed = result.Failed, error = result.Error });
        }, cancellationToken);
}

file static class WorkspaceCommandHttp
{
    public static async Task<IResult> RunExclusiveAsync(
        IWorkspaceOperationRunner runner,
        int workspaceId,
        string displayMessage,
        Func<IProgress<OperationProgress>, CancellationToken, Task<IResult>> work,
        CancellationToken cancellationToken)
    {
        IResult? result = null;
        var started = runner.TryStart(
            workspaceId,
            "workspace",
            WorkspaceJobKeys.RepositoriesOverlayKey(workspaceId),
            displayMessage,
            async (op, ct) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                result = await work(op.ToOperationProgress(), linked.Token);
            },
            out var operation);

        if (!started)
            return Results.Conflict("A workspace operation is already running.");

        await operation.WhenCompleted;
        return result ?? Results.Problem("Workspace operation failed.", statusCode: 500);
    }
}

public sealed class UpdateWorkspaceApiRequest
{
    public IReadOnlyList<int>? RepositoryIds { get; set; }
    public string? CommitMessage { get; set; }
    public bool IncludeDepsInCommitMessage { get; set; } = true;
    public int? MaxLevel { get; set; }
}

public sealed class PushWorkspaceApiRequest
{
    public IReadOnlyList<int>? RepositoryIds { get; set; }
    public IReadOnlyList<string>? RequiredPackageIds { get; set; }
    public bool SynchronizedPush { get; set; } = true;
}

public sealed class NewFeatureApiRequest
{
    public string? NewBranchName { get; set; }
    public string? BaseBranch { get; set; }
    public IReadOnlyList<int>? RepositoryIds { get; set; }
    public bool UpdateDependencies { get; set; }
    public bool PushChanges { get; set; }
    public string? CommitMessage { get; set; }
}

public sealed class SyncWorkspaceApiRequest
{
    public IReadOnlyList<int>? RepositoryIds { get; set; }
}

public sealed class SyncToDefaultApiRequest
{
    public IReadOnlyList<int>? RepositoryIds { get; set; }
}

public sealed class PullWorkspaceApiRequest
{
    public int RepositoryId { get; set; }
    public IReadOnlyList<int>? RepositoryIds { get; set; }
}

public sealed class UndoPushApiRequest
{
    public bool KeepChanges { get; set; } = true;
}

public sealed class CreatePullRequestsApiRequest
{
    public IReadOnlyList<CreatePullRequestRequest>? Requests { get; set; }
}

public sealed class MergePullRequestApiRequest
{
    public int RepositoryId { get; set; }
    public int PullRequestNumber { get; set; }
    public MergeMethod Method { get; set; }
    public string? ExpectedHeadSha { get; set; }
}

public sealed class GitChangesCommitApiRequest
{
    public int RepositoryId { get; set; }
    public string? Message { get; set; }
    public bool StageAllFirst { get; set; }
}

public sealed class GitChangesPathsApiRequest
{
    public int RepositoryId { get; set; }
    public GitChangeOperationScope Scope { get; set; }
    public IReadOnlyList<string>? Paths { get; set; }
}
