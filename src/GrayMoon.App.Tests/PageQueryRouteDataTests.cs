using System.Reflection;
using GrayMoon.App.Components;
using GrayMoon.App.Components.Pages;
using Microsoft.AspNetCore.Components;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace GrayMoon.App.Tests;

/// <summary>
/// A page's Workspace (route) and <c>?context=</c> (query) must reach it in one parameter update. With
/// <c>[SupplyParameterFromQuery]</c> the query was pushed on its own when a navigation started, next to the
/// previous route values: switching from a Feature of one Workspace to another Workspace made the page load
/// the first Workspace without its Feature, then the target (seen 2026-11-18).
/// </summary>
public sealed class PageQueryRouteDataTests
{
    private static readonly IReadOnlyDictionary<string, object?> Workspace1 =
        new Dictionary<string, object?> { ["WorkspaceId"] = 1 };

    [Fact]
    public void Query_values_are_passed_next_to_the_route_values()
    {
        var routeData = new RouteData(typeof(WorkspaceGitChanges), Workspace1);

        var result = PageQueryRouteData.WithQueryParameters(
            routeData, "http://localhost/workspaces/1/changes?q=repo%3AEdx1.IO&context=244#top");

        Assert.Equal(typeof(WorkspaceGitChanges), result.PageType);
        Assert.Equal(1, result.RouteValues["WorkspaceId"]);
        Assert.Equal(244, result.RouteValues["ContextQuery"]);
        Assert.Equal("repo:Edx1.IO", result.RouteValues["IncomingFilterQuery"]);
    }

    // The page instance is reused across navigations and an omitted parameter keeps its old value, so leaving
    // ?context= out of the URL must reach the page as null (this is the switch from a Feature to a Workspace).
    [Theory]
    [InlineData("http://localhost/workspaces/1/changes")]
    [InlineData("http://localhost/workspaces/1/changes?other=5")]
    [InlineData("http://localhost/workspaces/1/changes?context=")]
    [InlineData("http://localhost/workspaces/1/changes?context=abc")]
    public void An_absent_or_invalid_query_value_is_passed_as_null(string uri)
    {
        var routeData = new RouteData(typeof(WorkspaceGitChanges), Workspace1);

        var result = PageQueryRouteData.WithQueryParameters(routeData, uri);

        Assert.True(result.RouteValues.ContainsKey("ContextQuery"));
        Assert.Null(result.RouteValues["ContextQuery"]);
        Assert.True(result.RouteValues.ContainsKey("IncomingFilterQuery"));
        Assert.Null(result.RouteValues["IncomingFilterQuery"]);
    }

    [Fact]
    public void A_page_without_query_parameters_keeps_its_route_data()
    {
        var routeData = new RouteData(typeof(Workspaces), new Dictionary<string, object?>());

        Assert.Same(routeData, PageQueryRouteData.WithQueryParameters(routeData, "http://localhost/workspaces?context=244"));
    }

    [Fact]
    public void No_routable_page_takes_a_query_value_through_SupplyParameterFromQuery()
    {
        var offenders = RoutablePages()
            .SelectMany(page => InstanceProperties(page)
                .Where(property => property.GetCustomAttribute<SupplyParameterFromQueryAttribute>() is not null)
                .Select(property => $"{page.Name}.{property.Name}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Use [Parameter, PageQuery(\"name\")] so the value arrives with the route values: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_PageQuery_property_is_on_a_routable_page_and_binds()
    {
        var pages = RoutablePages().ToList();
        var bound = pages.Where(page => InstanceProperties(page).Any(p => p.GetCustomAttribute<PageQueryAttribute>() is not null)).ToList();

        // The Workspace pages all carry ?context=.
        Assert.Contains(typeof(WorkspaceRepositories), bound);
        Assert.Contains(typeof(WorkspaceGitChanges), bound);
        Assert.Contains(typeof(WorkspaceActions), bound);
        Assert.Contains(typeof(WorkspaceProjects), bound);
        Assert.Contains(typeof(WorkspaceDependencies), bound);
        Assert.Contains(typeof(WorkspacePackages), bound);
        Assert.Contains(typeof(WorkspaceFiles), bound);

        // Throws for a [PageQuery] property without [Parameter] or of an unsupported type.
        foreach (var page in bound)
        {
            var result = PageQueryRouteData.WithQueryParameters(
                new RouteData(page, Workspace1), "http://localhost/x?context=7");
            Assert.Equal(7, result.RouteValues["ContextQuery"]);
        }

        var strays = typeof(PageQueryAttribute).Assembly.GetTypes()
            .Where(type => !pages.Contains(type))
            .Where(type => InstanceProperties(type).Any(p => p.GetCustomAttribute<PageQueryAttribute>() is not null))
            .Select(type => type.Name)
            .ToList();
        Assert.Empty(strays);
    }

    private static IEnumerable<Type> RoutablePages()
        => typeof(PageQueryAttribute).Assembly.GetTypes()
            .Where(type => type.GetCustomAttributes<RouteAttribute>().Any());

    private static IEnumerable<PropertyInfo> InstanceProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
}
