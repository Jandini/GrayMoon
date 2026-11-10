namespace GrayMoon.App.Components.Pages;

/// <summary>
/// Decides whether data loaded off the dispatcher (a job thread, a hub callback) may still be assigned to page
/// fields. The apply step runs inside <c>InvokeAsync</c> and asks this first: a disposed page, or a page whose
/// Feature context changed since the load started, must drop the result instead of overwriting the new grid.
/// </summary>
internal static class PageStateApplyGuard
{
    public static bool ShouldApply(bool disposed, int capturedContextGeneration, int currentContextGeneration)
        => !disposed && capturedContextGeneration == currentContextGeneration;
}

/// <summary>
/// Decides when a page attaches its sync hub connection. The first render happens at the first real await of
/// the page load, usually before the data is there, so the decision is taken on every render until the
/// connection exists - never on <c>firstRender</c> alone.
/// </summary>
internal static class PageRealtimeGate
{
    public static bool ShouldAttachHub(bool hubAttached, bool dataLoaded, bool disposed)
        => !hubAttached && dataLoaded && !disposed;

    /// <summary>One-time work that needs loaded rows (the on-open background refresh).</summary>
    public static bool ShouldStartOnce(bool alreadyStarted, bool isLoading, bool hasRows, bool disposed)
        => !alreadyStarted && !isLoading && hasRows && !disposed;
}
