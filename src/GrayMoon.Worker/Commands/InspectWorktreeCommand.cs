using GrayMoon.Worker.Services;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Commands;

public sealed class InspectWorktreeCommand(IGitWorktreeService worktreeService)
    : ICommandHandler<InspectWorktreeRequest, InspectWorktreeResponse>
{
    public async Task<InspectWorktreeResponse> ExecuteAsync(InspectWorktreeRequest request, CancellationToken cancellationToken = default)
    {
        var mainPath = request.MainRepositoryPath ?? throw new ArgumentException("mainRepositoryPath required");
        var worktreePath = request.WorktreePath ?? throw new ArgumentException("worktreePath required");

        var result = await worktreeService.InspectWorktreeAsync(mainPath, worktreePath, request.DefaultBranch, request.FeatureBranch, cancellationToken);

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
            FeatureBranchExists = result.FeatureBranchExists,
            FeatureBranchSha = result.FeatureBranchSha,
            FeatureBranchAheadOfDefault = result.FeatureBranchAheadOfDefault,
            FeatureBranchHasUpstream = result.FeatureBranchHasUpstream,
            FeatureBranchAheadOfUpstream = result.FeatureBranchAheadOfUpstream,
        };
    }
}
