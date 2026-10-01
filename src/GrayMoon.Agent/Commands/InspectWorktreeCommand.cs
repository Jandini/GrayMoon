using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Jobs.Response;

namespace GrayMoon.Agent.Commands;

public sealed class InspectWorktreeCommand(IGitService git)
    : ICommandHandler<InspectWorktreeRequest, InspectWorktreeResponse>
{
    public async Task<InspectWorktreeResponse> ExecuteAsync(InspectWorktreeRequest request, CancellationToken cancellationToken = default)
    {
        var mainPath = request.MainRepositoryPath ?? throw new ArgumentException("mainRepositoryPath required");
        var worktreePath = request.WorktreePath ?? throw new ArgumentException("worktreePath required");

        var result = await git.InspectWorktreeAsync(mainPath, worktreePath, request.DefaultBranch, cancellationToken);

        return new InspectWorktreeResponse
        {
            IsRegistered = result.IsRegistered,
            Exists = result.Exists,
            IsLocked = result.IsLocked,
            LockReason = result.LockReason,
            HeadSha = result.HeadSha,
            Branch = result.Branch,
            IsDirty = result.IsDirty,
            StagedCount = result.StagedCount,
            UnstagedCount = result.UnstagedCount,
            UntrackedCount = result.UntrackedCount,
            ConflictCount = result.ConflictCount,
            HasUpstream = result.HasUpstream,
            AheadOfUpstream = result.AheadOfUpstream,
            BehindUpstream = result.BehindUpstream,
            AheadOfDefault = result.AheadOfDefault,
            Error = result.Error,
        };
    }
}
