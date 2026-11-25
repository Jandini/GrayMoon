using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>Sets or releases a named hold on the Checkout syncs kept back during worktree creation.</summary>
public sealed class HoldWorktreeHookSyncsCommand(IWorktreeCreateHookDeferral deferral)
    : ICommandHandler<HoldWorktreeHookSyncsRequest, HoldWorktreeHookSyncsResponse>
{
    public Task<HoldWorktreeHookSyncsResponse> ExecuteAsync(HoldWorktreeHookSyncsRequest request, CancellationToken cancellationToken = default)
    {
        var key = request.Key?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("key required");

        if (request.Hold)
            deferral.Hold(key);
        else
            deferral.Release(key);
        return Task.FromResult(new HoldWorktreeHookSyncsResponse { Success = true });
    }
}
