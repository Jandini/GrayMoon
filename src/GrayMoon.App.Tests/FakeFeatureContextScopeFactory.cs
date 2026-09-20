using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>Minimal scope factory that serves a single Feature context id to Git Changes warm-up / +/- tests.</summary>
internal sealed class FakeFeatureContextScopeFactory(WorkspaceFeatureContextId contextId) : IServiceScopeFactory
{
    public IServiceScope CreateScope() => new Scope(contextId);

    private sealed class Scope(WorkspaceFeatureContextId contextId) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new Provider(contextId);
        public void Dispose() { }
    }

    private sealed class Provider(WorkspaceFeatureContextId contextId) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IWorkspaceFeatureContextResolver))
                return new Resolver(contextId);
            return null;
        }
    }

    private sealed class Resolver(WorkspaceFeatureContextId contextId) : IWorkspaceFeatureContextResolver
    {
        public Task<WorkspaceFeatureContextId> GetOrCreateSpecialWorkspaceContextIdAsync(
            int workspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult(contextId);

        public Task<WorkspaceFeatureContextInfo> GetRequiredAsync(
            WorkspaceFeatureContextId id, int? expectedWorkspaceId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceFeatureContextInfo
            {
                ContextId = id,
                WorkspaceId = expectedWorkspaceId ?? 1,
                IsSpecialWorkspace = true
            });

        public Task<IReadOnlyList<WorkspaceFeatureContextInfo>> ListForWorkspaceAsync(
            int workspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceFeatureContextInfo>>([
                new WorkspaceFeatureContextInfo
                {
                    ContextId = contextId,
                    WorkspaceId = workspaceId,
                    IsSpecialWorkspace = true
                }
            ]);
    }
}
