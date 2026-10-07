using GrayMoon.Worker.Services;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Commands;

public sealed class ListGitWorktreesCommand(IGitWorktreeService worktreeService)
    : ICommandHandler<ListGitWorktreesRequest, ListGitWorktreesResponse>
{
    public async Task<ListGitWorktreesResponse> ExecuteAsync(ListGitWorktreesRequest request, CancellationToken cancellationToken = default)
    {
        var mainPath = request.MainRepositoryPath ?? throw new ArgumentException("mainRepositoryPath required");

        var (success, worktrees, errorCode, errorMessage) = await worktreeService.ListWorktreesAsync(mainPath, cancellationToken);
        return new ListGitWorktreesResponse
        {
            Success = success,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            Worktrees = success ? worktrees.ToList() : null,
        };
    }
}
