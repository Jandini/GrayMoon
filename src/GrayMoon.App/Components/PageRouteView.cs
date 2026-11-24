using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace GrayMoon.App.Components;

/// <summary>
/// <see cref="RouteView"/> that gives a page marked <see cref="KeepsInstanceOnQueryChangeAttribute"/> a key
/// (<see cref="PageQueryRouteData.InstanceKey"/>). The framework replaces an interactive root component without
/// a key whenever its parameters change, and keeps one with a key. Every other page renders as in
/// <see cref="RouteView"/>.
/// </summary>
public sealed class PageRouteView : RouteView
{
    protected override void Render(RenderTreeBuilder builder)
    {
        var key = PageQueryRouteData.InstanceKey(RouteData);
        if (key is null)
        {
            base.Render(builder);
            return;
        }

        var layout = RouteData.PageType.GetCustomAttribute<LayoutAttribute>()?.LayoutType ?? DefaultLayout;
        builder.OpenComponent<LayoutView>(0);
        builder.AddComponentParameter(1, nameof(LayoutView.Layout), layout);
        builder.AddComponentParameter(2, nameof(LayoutView.ChildContent), (RenderFragment)(page =>
        {
            page.OpenComponent(0, RouteData.PageType);
            foreach (var routeValue in RouteData.RouteValues)
                page.AddComponentParameter(1, routeValue.Key, routeValue.Value);
            page.SetKey(key);
            page.CloseComponent();
        }));
        builder.CloseComponent();
    }
}
