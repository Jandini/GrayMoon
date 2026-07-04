namespace GrayMoon.App.Components.Shared;

/// <summary>Shared virtual-scroll UI helpers (stable striping independent of DOM position).</summary>
public static class VirtualScrollUi
{
    /// <summary>Matches Bootstrap table-striped: first data row (index 0) is odd/striped.</summary>
    public static string StripeClass(int absoluteIndex) =>
        (absoluteIndex & 1) == 0 ? "virtual-row-odd" : "virtual-row-even";
}
