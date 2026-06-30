namespace GrayMoon.App.Services;

/// <summary>
/// Executes an action against a single scoped service obtained from a fresh DI scope.
/// Registered as a singleton; each call creates and disposes its own AsyncScope.
/// </summary>
public interface IScopedServiceExecutor
{
    /// <summary>Creates a fresh scope, resolves TService, and awaits the action.</summary>
    Task ExecuteAsync<TService>(
        Func<TService, Task> action,
        CancellationToken ct = default)
        where TService : notnull;

    /// <summary>Creates a fresh scope, resolves TService, and awaits the func, returning its result.</summary>
    Task<TResult> ExecuteAsync<TService, TResult>(
        Func<TService, Task<TResult>> func,
        CancellationToken ct = default)
        where TService : notnull;
}
