using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Commands;

public sealed class CleanupFeatureFolderCommand(FeatureFolderCleaner cleaner)
    : ICommandHandler<CleanupFeatureFolderRequest, CleanupFeatureFolderResponse>
{
    public async Task<CleanupFeatureFolderResponse> ExecuteAsync(CleanupFeatureFolderRequest request, CancellationToken cancellationToken = default)
    {
        var result = await cleaner.CleanupAsync(
            request.FeatureStorageRoot,
            request.FeatureRootPath,
            request.WorkspaceName,
            request.FeatureName,
            request.Retry,
            request.OnlyIfMarked,
            cancellationToken);

        return new CleanupFeatureFolderResponse
        {
            Outcome = result.Outcome.ToString(),
            RemainingFileCount = result.RemainingFileCount,
            Message = result.Message,
        };
    }
}
