using GrayMoon.App.Services.Application;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Commit All and Stage All fan out over many repositories at once through one <see cref="IWorkspaceGitChangesOperations"/>
/// instance (the page's scope). Each call used to read the workspace through the scope's single EF Core context, so two
/// calls overlapping in time threw "A second operation was started on this context instance" and the repository that lost
/// the race was never sent to the Worker. Seen in production: 7 dropped repositories in 133 runs, all in runs of 18 or more
/// repositories. These tests run that fan-out size against a real EF Core model.
/// </summary>
public sealed class WorkspaceGitChangesParallelCommitTests
{
    private const int Repositories = 32;

    [Fact]
    public async Task Parallel_commits_on_one_operations_instance_all_reach_the_worker()
    {
        var worker = new RecordingWorkerClient();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureServices: Register(worker));
        await using var scope = ctx.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceGitChangesOperations>();
        var contextId = await WorkspaceContextAsync(scope, ctx);

        var results = await Task.WhenAll(Enumerable.Range(0, Repositories).Select(_ =>
            Task.Run(() => operations.CommitAsync(ctx.WorkspaceId, contextId, ctx.RepositoryId, "message", stageAllFirst: true, CancellationToken.None))));

        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
        Assert.Equal(Repositories, worker.Commits);
    }

    [Fact]
    public async Task Parallel_stage_and_unstage_on_one_operations_instance_all_reach_the_worker()
    {
        var worker = new RecordingWorkerClient();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureServices: Register(worker));
        await using var scope = ctx.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceGitChangesOperations>();
        var contextId = await WorkspaceContextAsync(scope, ctx);

        var results = await Task.WhenAll(Enumerable.Range(0, Repositories).Select(i => Task.Run(() => i % 2 == 0
            ? operations.StageAsync(ctx.WorkspaceId, contextId, ctx.RepositoryId, GitChangeOperationScope.Repository, [], CancellationToken.None)
            : operations.UnstageAsync(ctx.WorkspaceId, contextId, ctx.RepositoryId, GitChangeOperationScope.Repository, [], CancellationToken.None))));

        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
        Assert.Equal(Repositories, worker.Stages + worker.Unstages);
    }

    [Fact]
    public async Task A_repository_that_is_not_in_the_workspace_fails_with_a_reason_instead_of_throwing()
    {
        var worker = new RecordingWorkerClient();
        await using var ctx = await SyncStateTestContext.CreateAsync(configureServices: Register(worker));
        await using var scope = ctx.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IWorkspaceGitChangesOperations>();
        var contextId = await WorkspaceContextAsync(scope, ctx);

        var result = await operations.CommitAsync(ctx.WorkspaceId, contextId, repositoryId: 987654, "message", stageAllFirst: true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Repository is not in the given workspace.", result.ErrorMessage);
        Assert.Equal(0, worker.Commits);
    }

    private static Action<IServiceCollection> Register(RecordingWorkerClient worker) => services =>
    {
        services.AddSingleton<IGitChangesWorkerClient>(worker);
        // The read service is only used by GetAsync, which these tests do not call.
        services.AddScoped<IWorkspaceGitChangesReadService>(_ => null!);
        services.AddScoped<IWorkspaceGitChangesOperations, WorkspaceGitChangesOperations>();
    };

    private static async Task<WorkspaceFeatureContextId> WorkspaceContextAsync(AsyncServiceScope scope, SyncStateTestContext ctx) =>
        await scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureContextResolver>()
            .GetOrCreateSpecialWorkspaceContextIdAsync(ctx.WorkspaceId);

    private sealed class RecordingWorkerClient : IGitChangesWorkerClient
    {
        private int _commits;
        private int _stages;
        private int _unstages;

        public int Commits => _commits;
        public int Stages => _stages;
        public int Unstages => _unstages;

        public async Task<GitChangesCommitResult> CommitAsync(
            string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
            string commitMessage, bool stageAllFirst, CancellationToken cancellationToken)
        {
            await Task.Delay(5, cancellationToken);
            Interlocked.Increment(ref _commits);
            return new GitChangesCommitResult { Success = true, CommitSha = "abc" };
        }

        public async Task<GitChangesMutationResult> StageAsync(
            string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
            GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            await Task.Delay(5, cancellationToken);
            Interlocked.Increment(ref _stages);
            return new GitChangesMutationResult { Success = true };
        }

        public async Task<GitChangesMutationResult> UnstageAsync(
            string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
            GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            await Task.Delay(5, cancellationToken);
            Interlocked.Increment(ref _unstages);
            return new GitChangesMutationResult { Success = true };
        }

        public Task<GitChangesStatusResult> GetStatusAsync(
            string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
            int workspaceId, int repositoryId, CancellationToken cancellationToken, bool includeLineStats = false) =>
            throw new NotSupportedException();

        public Task<GitChangesDiffResult> GetDiffAsync(
            string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
            string path, GitDiffComparison comparison, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitChangesMutationResult> DiscardAsync(
            string workspaceRoot, string workspaceName, string repositoryName, string? workspaceRepositoryName,
            GitChangeOperationScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
