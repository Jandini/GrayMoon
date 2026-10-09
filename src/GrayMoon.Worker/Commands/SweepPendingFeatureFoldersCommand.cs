using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Deletes the Feature folders marked pending deletion under one storage root, one quick pass each (no per-file retries:
/// a folder still in use simply stays marked until the next sweep).
/// </summary>
public sealed class SweepPendingFeatureFoldersCommand(FeatureFolderCleaner cleaner)
    : ICommandHandler<SweepPendingFeatureFoldersRequest, SweepPendingFeatureFoldersResponse>
{
    public async Task<SweepPendingFeatureFoldersResponse> ExecuteAsync(SweepPendingFeatureFoldersRequest request, CancellationToken cancellationToken = default)
    {
        var storageRoot = request.FeatureStorageRoot ?? throw new ArgumentException("featureStorageRoot required");
        var response = new SweepPendingFeatureFoldersResponse();

        foreach (var folder in cleaner.FindMarkedFolders(storageRoot, request.ExcludeFeatureNames ?? []))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await cleaner.CleanupAsync(storageRoot, folder, request.WorkspaceName, featureName: null, retry: false, requireMarker: true, cancellationToken);
            switch (result.Outcome)
            {
                case FeatureFolderCleanupOutcome.Removed:
                    response.Removed++;
                    break;
                case FeatureFolderCleanupOutcome.PendingDeletion:
                    response.StillPending++;
                    break;
                case FeatureFolderCleanupOutcome.NotMarked:
                    break;
                default:
                    response.Refused++;
                    break;
            }
        }

        return response;
    }
}
