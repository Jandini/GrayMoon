using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Services;

public sealed class ScopedServiceExecutor(IServiceScopeFactory scopeFactory) : IScopedServiceExecutor
{
    public async Task ExecuteAsync<TService>(
        Func<TService, Task> action,
        CancellationToken ct = default)
        where TService : notnull
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TService>();
        await action(service);
    }

    public async Task<TResult> ExecuteAsync<TService, TResult>(
        Func<TService, Task<TResult>> func,
        CancellationToken ct = default)
        where TService : notnull
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TService>();
        return await func(service);
    }
}
