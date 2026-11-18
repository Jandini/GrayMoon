using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace GrayMoon.App.Components;

/// <summary>
/// Adds a page's <see cref="PageQueryAttribute"/> values to its route values, so <c>RouteView</c> passes the
/// query-bound parameters to the page as direct parameters. See <see cref="PageQueryAttribute"/> for why.
/// </summary>
public static class PageQueryRouteData
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<QueryBinding>> BindingsByPageType = new();

    /// <summary>
    /// Returns <paramref name="routeData"/> itself for a page without query-bound parameters, else a copy whose
    /// route values also hold every query-bound parameter. A key that is absent (or not a valid value for the
    /// property) is passed as null, never omitted: an omitted parameter keeps its previous value on a page
    /// instance that is reused across navigations.
    /// </summary>
    public static RouteData WithQueryParameters(RouteData routeData, string uri)
    {
        ArgumentNullException.ThrowIfNull(routeData);
        ArgumentNullException.ThrowIfNull(uri);

        var bindings = BindingsByPageType.GetOrAdd(routeData.PageType, ReadBindings);
        if (bindings.Count == 0)
            return routeData;

        var query = QueryHelpers.ParseQuery(QueryOf(uri));
        var values = new Dictionary<string, object?>(routeData.RouteValues, StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            var raw = query.TryGetValue(binding.QueryName, out var found) ? found.FirstOrDefault() : null;
            values[binding.PropertyName] = binding.Convert(raw);
        }

        return new RouteData(routeData.PageType, values);
    }

    private static string QueryOf(string uri)
    {
        var start = uri.IndexOf('?');
        if (start < 0)
            return string.Empty;

        var end = uri.IndexOf('#', start);
        return end < 0 ? uri[start..] : uri[start..end];
    }

    private static IReadOnlyList<QueryBinding> ReadBindings(Type pageType)
    {
        var bindings = new List<QueryBinding>();
        foreach (var property in pageType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<PageQueryAttribute>() is not { } attribute)
                continue;

            if (property.GetCustomAttribute<ParameterAttribute>() is null)
            {
                throw new InvalidOperationException(
                    $"{pageType.Name}.{property.Name} has [PageQuery] but not [Parameter].");
            }

            bindings.Add(new QueryBinding(property.Name, attribute.Name, ConverterFor(pageType, property)));
        }

        return bindings;
    }

    private static Func<string?, object?> ConverterFor(Type pageType, PropertyInfo property)
    {
        if (property.PropertyType == typeof(string))
            return raw => raw;

        if (property.PropertyType == typeof(int?))
        {
            return raw => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        throw new NotSupportedException(
            $"{pageType.Name}.{property.Name}: [PageQuery] supports int? and string, not {property.PropertyType.Name}.");
    }

    private sealed record QueryBinding(string PropertyName, string QueryName, Func<string?, object?> Convert);
}
