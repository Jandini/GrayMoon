namespace GrayMoon.App.Services.Features;

/// <summary>
/// Per Feature context coordination for the two things that must not overlap while a Feature is being created or
/// repaired: the initial projection seed and the hook-driven syncs the Worker releases once the worktrees exist.
/// </summary>
/// <remarks>
/// <para>
/// <b>Finalization barrier.</b> <see cref="Arm"/> is called before the first worktree of a Create or Repair and its
/// scope is disposed (in a <c>finally</c>) after the seed and the lifecycle change. Until then
/// <see cref="WaitForFinalizationAsync"/> does not return, so a deferred checkout sync for that context applies its
/// Feature-specific GitVersion only after the graph exists, and the seed never overwrites it. The barrier is in
/// memory only: a crash or restart drops it, and the idempotent seed in Repair reconciles whatever Sync wrote.
/// </para>
/// <para>
/// <b>Projection gate.</b> <see cref="AcquireProjectionAsync"/> serializes every writer of a context's
/// <c>WorkspaceProjects</c> and <c>ProjectDependencies</c> (the seed and the project merges), so the unique
/// (context, repository, name) index is never raced by a lookup-then-insert on the same context.
/// </para>
/// </remarks>
public sealed class FeatureFinalizationCoordinator
{
    /// <summary>Upper bound for a waiting sync, so a missed release can never stall the sync queue forever.</summary>
    internal static readonly TimeSpan MaxBarrierWait = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly Dictionary<int, Barrier> _barriers = [];
    private readonly Dictionary<int, SemaphoreSlim> _projectionGates = [];

    private sealed class Barrier
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Holders { get; set; }
    }

    /// <summary>Marks the context as finalizing until the returned scope is disposed. Nested arms share one barrier.</summary>
    public IDisposable Arm(int contextId)
    {
        lock (_gate)
        {
            if (!_barriers.TryGetValue(contextId, out var barrier))
                _barriers[contextId] = barrier = new Barrier();
            barrier.Holders++;
        }

        return new ArmScope(this, contextId);
    }

    public bool IsFinalizing(int contextId)
    {
        lock (_gate)
            return _barriers.ContainsKey(contextId);
    }

    /// <summary>Completes immediately when the context is not finalizing.</summary>
    public async Task WaitForFinalizationAsync(int contextId, CancellationToken cancellationToken = default)
    {
        Task wait;
        lock (_gate)
        {
            if (!_barriers.TryGetValue(contextId, out var barrier))
                return;
            wait = barrier.Completion.Task;
        }

        await wait.WaitAsync(MaxBarrierWait, cancellationToken);
    }

    /// <summary>Takes the context's projection write gate. Not reentrant: do not call a gated writer while holding it.</summary>
    public async Task<IDisposable> AcquireProjectionAsync(int contextId, CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate;
        lock (_gate)
        {
            if (!_projectionGates.TryGetValue(contextId, out gate!))
                _projectionGates[contextId] = gate = new SemaphoreSlim(1, 1);
        }

        await gate.WaitAsync(cancellationToken);
        return new GateScope(gate);
    }

    private void Release(int contextId)
    {
        Barrier? done = null;
        lock (_gate)
        {
            if (_barriers.TryGetValue(contextId, out var barrier) && --barrier.Holders <= 0)
            {
                _barriers.Remove(contextId);
                done = barrier;
            }
        }

        done?.Completion.TrySetResult();
    }

    private sealed class ArmScope(FeatureFinalizationCoordinator owner, int contextId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(contextId);
        }
    }

    private sealed class GateScope(SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                gate.Release();
        }
    }
}
