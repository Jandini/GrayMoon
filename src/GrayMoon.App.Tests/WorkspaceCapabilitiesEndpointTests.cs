using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Api.Endpoints;
using GrayMoon.Application.Workspaces;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GrayMoon.App.Tests;

/// <summary>
/// <c>GET /workspaces/{id}/capabilities</c> is how the Worker learns a workspace's profile on the git-hook
/// path, where it has no app request to read it from. It returns the Worker-facing subset only.
/// </summary>
public sealed class WorkspaceCapabilitiesEndpointTests
{
    private sealed class StubResolver(WorkspaceCapabilities? capabilities) : IWorkspaceCapabilitiesResolver
    {
        public Task<WorkspaceCapabilities> GetAsync(int workspaceId, CancellationToken cancellationToken = default)
            => capabilities is null
                ? throw new InvalidOperationException($"Workspace {workspaceId} does not exist.")
                : Task.FromResult(capabilities);

        public Task<IReadOnlyDictionary<int, WorkspaceCapabilities>> GetManyAsync(
            IReadOnlyCollection<int> workspaceIds,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static async Task<RepositoryOperationCapabilities> OkBodyAsync(WorkspaceCapabilities capabilities)
    {
        var result = await WorkspaceEndpoints.GetWorkspaceCapabilities(7, new StubResolver(capabilities), CancellationToken.None);
        var ok = Assert.IsType<Ok<RepositoryOperationCapabilities>>(result.Result);
        Assert.NotNull(ok.Value);
        return ok.Value!;
    }

    [Fact]
    public async Task A_basic_workspace_without_versioning_asks_the_worker_for_neither_enrichment()
    {
        var body = await OkBodyAsync(new WorkspaceCapabilities(
            WorkspaceType.Basic,
            WorkspaceVersioningMode.None,
            WorkspaceCiProvider.None));

        Assert.False(body.CalculateRepositoryVersion);
        Assert.False(body.DiscoverDotNetProjects);
    }

    [Fact]
    public async Task A_basic_workspace_with_gitversion_asks_for_the_version_only()
    {
        var body = await OkBodyAsync(new WorkspaceCapabilities(
            WorkspaceType.Basic,
            WorkspaceVersioningMode.GitVersion,
            WorkspaceCiProvider.None));

        Assert.True(body.CalculateRepositoryVersion);
        Assert.False(body.DiscoverDotNetProjects);
    }

    [Fact]
    public async Task An_existing_dotnet_workspace_asks_for_both()
    {
        var body = await OkBodyAsync(WorkspaceCapabilities.Legacy);

        Assert.True(body.CalculateRepositoryVersion);
        Assert.True(body.DiscoverDotNetProjects);
    }

    [Fact]
    public async Task A_workspace_that_does_not_exist_is_a_404()
    {
        var result = await WorkspaceEndpoints.GetWorkspaceCapabilities(7, new StubResolver(null), CancellationToken.None);

        Assert.IsType<NotFound>(result.Result);
    }

    [Fact]
    public async Task A_non_positive_workspace_id_is_rejected_before_any_lookup()
    {
        var result = await WorkspaceEndpoints.GetWorkspaceCapabilities(0, new StubResolver(null), CancellationToken.None);

        Assert.IsType<BadRequest<Microsoft.AspNetCore.Mvc.ProblemDetails>>(result.Result);
    }
}
