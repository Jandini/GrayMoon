namespace GrayMoon.App.Components;

/// <summary>
/// Marks a routable page whose instance survives a navigation that only changes its <see cref="PageQueryAttribute"/>
/// values (for example <c>?context=</c>). <see cref="PageRouteView"/> then renders the page with a key made of
/// its type and its route values, so Blazor sets the new parameters on the existing instance instead of
/// disposing it and creating a new one (which loaded the page a second time and reconnected its hub).
/// </summary>
/// <remarks>
/// Only for a page that applies a changed query value in <c>OnParametersSet(Async)</c>. A page that loads only in
/// <c>OnInitializedAsync</c> would keep showing the previous context. A changed route value (another Workspace)
/// changes the key, so that navigation still creates a new instance.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class KeepsInstanceOnQueryChangeAttribute : Attribute;
