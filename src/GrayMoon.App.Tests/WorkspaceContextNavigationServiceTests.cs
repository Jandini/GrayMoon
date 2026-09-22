using GrayMoon.App.Services.Features;
using GrayMoon.Application.Features;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceContextNavigationServiceTests
{
    [Fact]
    public async Task Foreign_context_query_falls_back_to_this_workspace_and_drops_the_query()
    {
        var resolver = new FakeResolver();
        var selected = new FakeSelected(new WorkspaceFeatureContextId(1));
        var navigation = new TestNavigationManager("http://localhost/workspaces/1/changes?context=34&q=repo:EDX1");
        var service = new WorkspaceContextNavigationService(resolver, selected, navigation, NullLogger<WorkspaceContextNavigationService>.Instance);

        var info = await service.ResolveForPageAsync(workspaceId: 1, contextQuery: 34);

        Assert.Equal(1, info.ContextId.Value);
        Assert.Equal(1, info.WorkspaceId);
        Assert.DoesNotContain("context=", navigation.Uri, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("q=repo", navigation.Uri, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(selected.SetCalls);
    }

    [Fact]
    public async Task Owned_context_query_is_selected()
    {
        var resolver = new FakeResolver();
        var selected = new FakeSelected(new WorkspaceFeatureContextId(1));
        var navigation = new TestNavigationManager("http://localhost/workspaces/19/changes?context=34");
        var service = new WorkspaceContextNavigationService(resolver, selected, navigation, NullLogger<WorkspaceContextNavigationService>.Instance);

        var info = await service.ResolveForPageAsync(workspaceId: 19, contextQuery: 34);

        Assert.Equal(34, info.ContextId.Value);
        Assert.Equal([(19, 34)], selected.SetCalls);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string uri) => Initialize("http://localhost/", uri);

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            Uri = ToAbsoluteUri(uri).ToString();
        }
    }

    private sealed class FakeResolver : IWorkspaceFeatureContextResolver
    {
        public Task<WorkspaceFeatureContextId> GetOrCreateSpecialWorkspaceContextIdAsync(int workspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceFeatureContextId(workspaceId == 1 ? 1 : 33));

        public Task<WorkspaceFeatureContextInfo> GetRequiredAsync(WorkspaceFeatureContextId contextId, int? expectedWorkspaceId = null, CancellationToken cancellationToken = default)
        {
            var owner = contextId.Value switch
            {
                34 => 19,
                1 => 1,
                33 => 19,
                _ => throw new InvalidOperationException($"WorkspaceFeatureContext {contextId.Value} was not found.")
            };
            if (expectedWorkspaceId is int workspaceId && owner != workspaceId)
                throw new InvalidOperationException($"WorkspaceFeatureContext {contextId.Value} does not belong to workspace {workspaceId}.");

            return Task.FromResult(new WorkspaceFeatureContextInfo
            {
                ContextId = contextId,
                WorkspaceId = owner,
                IsSpecialWorkspace = contextId.Value is 1 or 33,
                FeatureName = contextId.Value == 34 ? "cpio-expander" : null
            });
        }

        public Task<IReadOnlyList<WorkspaceFeatureContextInfo>> ListForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceFeatureContextInfo>>([]);
    }

    private sealed class FakeSelected(WorkspaceFeatureContextId? selected) : IWorkspaceSelectedFeatureContextService
    {
        public List<(int WorkspaceId, int ContextId)> SetCalls { get; } = [];

        public Task<WorkspaceFeatureContextId?> GetSelectedAsync(int workspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult(selected);

        public Task SetSelectedAsync(int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default)
        {
            SetCalls.Add((workspaceId, contextId.Value));
            return Task.CompletedTask;
        }
    }
}
