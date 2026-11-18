namespace GrayMoon.App.Components;

/// <summary>
/// Marks a page's <c>[Parameter]</c> property as bound to a query-string value. <see cref="PageQueryRouteData"/>
/// reads it during the server render and hands it to the page next to its route values, so both reach an
/// interactive page in the same parameter update.
/// </summary>
/// <remarks>
/// Do not use <c>[SupplyParameterFromQuery]</c> on a routable page. Pages here are interactive root components
/// under a static router: route values arrive with the root-component update after the navigation's response,
/// while <c>[SupplyParameterFromQuery]</c> is pushed from the circuit's location change when the navigation
/// starts, together with the previous route values. A page then sees a Workspace and a <c>?context=</c> that
/// never belonged together (a switch from a Feature of one Workspace to another Workspace first showed the
/// first Workspace without its Feature). Supported property types: <c>int?</c> and <c>string</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PageQueryAttribute(string name) : Attribute
{
    /// <summary>The query-string key, for example <c>context</c>.</summary>
    public string Name { get; } = name;
}
