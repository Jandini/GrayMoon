using System.Runtime.ExceptionServices;
using GrayMoon.App.Components;
using GrayMoon.App.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace GrayMoon.App.Tests;

#pragma warning disable BL0006 // RenderTreeFrame is the only way to read the key a component was rendered with.

/// <summary>
/// The Repositories grid loaded twice on every change of <c>?context=</c> (logs of 2026-11-25): the page is an
/// interactive root component, Blazor replaces a root component without a key when its parameters change, and
/// <c>ContextQuery</c> is one of its parameters. The old instance loaded the new context, then the new instance
/// loaded it again. A page marked <see cref="KeepsInstanceOnQueryChangeAttribute"/> is rendered with a key that
/// does not change with the query, so the instance is kept.
/// </summary>
public sealed class PageRouteViewTests
{
    [Fact]
    public void The_key_of_a_marked_page_ignores_the_query_values_and_follows_the_route_values()
    {
        var workspace = PageQueryRouteData.InstanceKey(Route<KeptPage>(1, "http://localhost/kept/1"));
        var feature = PageQueryRouteData.InstanceKey(Route<KeptPage>(1, "http://localhost/kept/1?context=44"));
        var otherWorkspace = PageQueryRouteData.InstanceKey(Route<KeptPage>(2, "http://localhost/kept/2?context=44"));

        Assert.NotNull(workspace);
        Assert.Equal(workspace, feature);
        Assert.NotEqual(workspace, otherWorkspace);
    }

    [Fact]
    public void A_page_that_is_not_marked_has_no_key()
        => Assert.Null(PageQueryRouteData.InstanceKey(Route<PlainPage>(1, "http://localhost/plain/1?context=44")));

    [Fact]
    public void The_Repositories_page_keeps_its_instance()
    {
        var key = PageQueryRouteData.InstanceKey(new RouteData(
            typeof(WorkspaceRepositories),
            new Dictionary<string, object?> { ["WorkspaceId"] = 14, ["ContextQuery"] = 44 }));

        Assert.Equal($"{typeof(WorkspaceRepositories).FullName}|workspaceid=14", key);
    }

    [Fact]
    public async Task A_marked_page_is_rendered_with_its_key_and_its_parameters()
    {
        var routeData = Route<KeptPage>(1, "http://localhost/kept/1?context=44");

        var frame = await RenderPageFrameAsync(routeData, typeof(KeptPage));

        Assert.Equal(PageQueryRouteData.InstanceKey(routeData), frame.Key);
        Assert.Equal(1, frame.Page.WorkspaceId);
        Assert.Equal(44, frame.Page.ContextQuery);
    }

    [Fact]
    public async Task A_page_that_is_not_marked_is_rendered_without_a_key()
    {
        var frame = await RenderPageFrameAsync(Route<PlainPage>(1, "http://localhost/plain/1?context=44"), typeof(PlainPage));

        Assert.Null(frame.Key);
        Assert.Equal(44, frame.Page.ContextQuery);
    }

    private static RouteData Route<TPage>(int workspaceId, string uri)
        => PageQueryRouteData.WithQueryParameters(
            new RouteData(typeof(TPage), new Dictionary<string, object?> { ["WorkspaceId"] = workspaceId }),
            uri);

    private static async Task<(object? Key, TestPage Page)> RenderPageFrameAsync(RouteData routeData, Type pageType)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        using var renderer = new FrameRenderer(services);
        var rootId = renderer.AddRoot(new PageRouteView());
        await renderer.RenderAsync(rootId, ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(RouteView.RouteData)] = routeData,
        }));

        var found = renderer.FindComponentFrame(rootId, pageType);
        Assert.NotNull(found);
        return (found.Value.ComponentKey, Assert.IsAssignableFrom<TestPage>(found.Value.Component));
    }

    public abstract class TestPage : ComponentBase
    {
        [Parameter] public int WorkspaceId { get; set; }
        [Parameter, PageQuery("context")] public int? ContextQuery { get; set; }
    }

    [KeepsInstanceOnQueryChange]
    public sealed class KeptPage : TestPage;

    public sealed class PlainPage : TestPage;

    private sealed class FrameRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public int AddRoot(IComponent component) => AssignRootComponentId(component);

        public Task RenderAsync(int componentId, ParameterView parameters)
            => Dispatcher.InvokeAsync(() => RenderRootComponentAsync(componentId, parameters));

        public RenderTreeFrame? FindComponentFrame(int componentId, Type componentType)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames.Array[i];
                if (frame.FrameType != RenderTreeFrameType.Component)
                    continue;
                if (frame.ComponentType == componentType)
                    return frame;
                if (FindComponentFrame(frame.ComponentId, componentType) is { } nested)
                    return nested;
            }

            return null;
        }

        protected override void HandleException(Exception exception)
            => ExceptionDispatchInfo.Capture(exception).Throw();

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }
}
