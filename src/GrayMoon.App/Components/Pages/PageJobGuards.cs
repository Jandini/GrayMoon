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

/// <summary>
/// What a context page needs to follow its <c>?context=</c> value: which value it is bound to, a generation for
/// loads that finish late, and one load at a time. A page binds when it resolved or switched its context, asks
/// <see cref="QueryChanged"/> in <c>OnParametersSetAsync</c> (browser back and forward change the URL without the
/// selector), runs every load through <see cref="RunLatestAsync"/>, and assigns what a load read only while
/// <see cref="IsCurrent"/> holds for the generation the load began with.
/// </summary>
/// <remarks>
/// Used on the dispatcher only. The bound value is the Feature context id, and null for the special Workspace,
/// whose URL carries no <c>?context=</c>: binding the Workspace's own context id instead would make the
/// selector's follow-up navigation look like a second switch.
/// </remarks>
internal sealed class PageContextBinding
{
    private bool _bound;
    private int? _boundQuery;
    private int _generation;
    private Task? _running;
    private bool _again;

    /// <summary>Counts context binds. A load that began under an older one must not assign its result.</summary>
    public int Generation => _generation;

    /// <summary>Binds the page to a context and starts a new generation.</summary>
    public int Bind(GrayMoon.Application.Features.WorkspaceFeatureContextId contextId, bool isSpecialWorkspace)
    {
        _bound = true;
        _boundQuery = isSpecialWorkspace ? null : contextId.Value;
        return ++_generation;
    }

    /// <summary>
    /// True when the URL names another context than the page is bound to. Never true before the first bind: until
    /// the page has resolved its context there is nothing to compare with, and the first resolve reads the URL itself.
    /// </summary>
    public bool QueryChanged(int? contextQuery)
        => _bound && _boundQuery != (contextQuery is > 0 ? contextQuery : null);

    public bool IsCurrent(int generation, bool disposed = false)
        => PageStateApplyGuard.ShouldApply(disposed, generation, _generation);

    /// <summary>
    /// Runs <paramref name="load"/> so that loads of one page never overlap (they share the page's scoped services)
    /// and the last one to run began after the latest request. A request that arrives while a load runs asks for
    /// one more run and completes with it; several such requests share that one run.
    /// </summary>
    public async Task RunLatestAsync(Func<Task> load)
    {
        if (_running is { } running)
        {
            _again = true;
            await running;
            return;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running = done.Task;
        try
        {
            do
            {
                _again = false;
                await load();
            }
            while (_again);
        }
        finally
        {
            _running = null;
            done.TrySetResult();
        }
    }
}
