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

    [Fact]
    public async Task Preferred_feature_context_canonicalizes_without_dropping_other_query()
    {
        var resolver = new FakeResolver();
        var selected = new FakeSelected(new WorkspaceFeatureContextId(34));
        var navigation = new TestNavigationManager("http://localhost/workspaces/19/changes?q=repo:EDX1");
        var service = new WorkspaceContextNavigationService(resolver, selected, navigation, NullLogger<WorkspaceContextNavigationService>.Instance);

        var info = await service.ResolveForPageAsync(workspaceId: 19, contextQuery: null);

        Assert.Equal(34, info.ContextId.Value);
        Assert.Contains("context=34", navigation.Uri, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("q=repo", navigation.Uri, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EDX1", Uri.UnescapeDataString(navigation.Uri), StringComparison.Ordinal);
    }

    // 2026-11-18: the Changes page ran with WorkspaceId 0 and the previous Workspace's ?context=244.
    // ResolveForPageAsync threw "Workspace 0 was not found" out of the page's parameter lifecycle and
    // ended the circuit ("Reload page").
    [Fact]
    public async Task ResolveForPage_throws_for_a_missing_workspace_and_TryResolveForPage_returns_null()
    {
        const string uri = "http://localhost/workspaces/1/changes?context=244";
        var navigation = new TestNavigationManager(uri);
        var selected = new FakeSelected(null);
        var service = new WorkspaceContextNavigationService(
            new FakeResolver(), selected, navigation, NullLogger<WorkspaceContextNavigationService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveForPageAsync(workspaceId: 404, contextQuery: null));

        Assert.Null(await service.TryResolveForPageAsync(workspaceId: 404, contextQuery: null));
    }

    [Fact]
    public async Task TryResolveForPage_without_a_workspace_id_resolves_nothing_and_keeps_url_and_selection()
    {
        const string uri = "http://localhost/workspaces/1/changes?context=244";
        var navigation = new TestNavigationManager(uri);
        var selected = new FakeSelected(null);
        var resolver = new FakeResolver();
        var service = new WorkspaceContextNavigationService(
            resolver, selected, navigation, NullLogger<WorkspaceContextNavigationService>.Instance);

        var info = await service.TryResolveForPageAsync(workspaceId: 0, contextQuery: 244);

        Assert.Null(info);
        Assert.Equal(uri, navigation.Uri);
        Assert.Empty(selected.SetCalls);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task TryResolveForPage_resolves_like_ResolveForPage_for_a_known_workspace()
    {
        var selected = new FakeSelected(new WorkspaceFeatureContextId(1));
        var service = new WorkspaceContextNavigationService(
            new FakeResolver(),
            selected,
            new TestNavigationManager("http://localhost/workspaces/19/changes?context=34"),
            NullLogger<WorkspaceContextNavigationService>.Instance);

        var info = await service.TryResolveForPageAsync(workspaceId: 19, contextQuery: 34);

        Assert.Equal(34, info?.ContextId.Value);
        Assert.Equal([(19, 34)], selected.SetCalls);
    }

    [Fact]
    public void AppendContextQuery_adds_context_for_feature_and_skips_workspace()
    {
        var service = new WorkspaceContextNavigationService(
            new FakeResolver(),
            new FakeSelected(null),
            new TestNavigationManager("http://localhost/workspaces/19"),
            NullLogger<WorkspaceContextNavigationService>.Instance);

        Assert.Equal(
            "/workspaces/19/changes?q=repo:api&context=34",
            service.AppendContextQuery("/workspaces/19/changes?q=repo:api", new WorkspaceFeatureContextId(34), isSpecialWorkspace: false));
        Assert.Equal(
            "/workspaces/19/changes?q=repo:api",
            service.AppendContextQuery("/workspaces/19/changes?q=repo:api", new WorkspaceFeatureContextId(33), isSpecialWorkspace: true));
    }

    [Fact]
    public async Task BuildWorkspaceEntryHref_includes_preferred_feature_context()
    {
        var service = new WorkspaceContextNavigationService(
            new FakeResolver(),
            new FakeSelected(new WorkspaceFeatureContextId(34)),
            new TestNavigationManager("http://localhost/workspaces"),
            NullLogger<WorkspaceContextNavigationService>.Instance);

        var href = await service.BuildWorkspaceEntryHrefAsync(19, "workspaces/19");

        Assert.Equal("workspaces/19?context=34", href);
    }

    [Fact]
    public async Task BuildWorkspaceEntryHref_omits_query_for_special_workspace()
    {
        var service = new WorkspaceContextNavigationService(
            new FakeResolver(),
            new FakeSelected(new WorkspaceFeatureContextId(33)),
            new TestNavigationManager("http://localhost/workspaces"),
            NullLogger<WorkspaceContextNavigationService>.Instance);

        var href = await service.BuildWorkspaceEntryHrefAsync(19, "workspaces/19");

        Assert.Equal("workspaces/19", href);
    }

    // Browser back and forward change ?context= while the page stays open. Four context pages read the value once,
    // so the grid kept the context that was left.
    [Theory]
    [InlineData(34, 34)]
    // No value means the Workspace, also when a Feature is the stored selection.
    [InlineData(null, 33)]
    [InlineData(0, 33)]
    // A context of another Workspace, or one that is gone.
    [InlineData(1, 33)]
    [InlineData(999, 33)]
    public async Task A_changed_context_query_selects_that_Feature_or_the_Workspace_and_never_the_stored_selection(int? query, int expected)
    {
        var selected = new FakeSelected(new WorkspaceFeatureContextId(34));
        var navigation = new TestNavigationManager("http://localhost/workspaces/19/actions?q=repo:EDX1");
        var service = new WorkspaceContextNavigationService(new FakeResolver(), selected, navigation, NullLogger<WorkspaceContextNavigationService>.Instance);

        var info = await service.ResolveQueryChangeAsync(workspaceId: 19, contextQuery: query);

        Assert.Equal(expected, info.ContextId.Value);
        Assert.Equal(expected == 33, info.IsSpecialWorkspace);
        Assert.Equal([(19, expected)], selected.SetCalls);
        // The URL is the user's: it is never rewritten here.
        Assert.Equal("http://localhost/workspaces/19/actions?q=repo:EDX1", navigation.Uri);
    }

    [Theory]
    [InlineData(34, 34)]
    [InlineData(999, 33)]
    public async Task A_selected_context_that_no_longer_belongs_to_the_Workspace_resolves_to_the_Workspace(int selectedId, int expected)
    {
        var selected = new FakeSelected(null);
        var service = new WorkspaceContextNavigationService(
            new FakeResolver(), selected, new TestNavigationManager("http://localhost/workspaces/19/files"), NullLogger<WorkspaceContextNavigationService>.Instance);

        var info = await service.ResolveSelectionAsync(19, new WorkspaceFeatureContextId(selectedId));

        Assert.Equal(expected, info.ContextId.Value);
        Assert.Empty(selected.SetCalls);
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
        public int Calls { get; private set; }

        public Task<WorkspaceFeatureContextId> GetOrCreateSpecialWorkspaceContextIdAsync(int workspaceId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return workspaceId is 1 or 19
                ? Task.FromResult(new WorkspaceFeatureContextId(workspaceId == 1 ? 1 : 33))
                : throw new InvalidOperationException($"Workspace {workspaceId} was not found.");
        }

        public Task<WorkspaceFeatureContextInfo> GetRequiredAsync(WorkspaceFeatureContextId contextId, int? expectedWorkspaceId = null, CancellationToken cancellationToken = default)
        {
            Calls++;
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
