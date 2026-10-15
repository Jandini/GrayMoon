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
    /// <summary>
    /// Upper bound on Feature folders deleted at once. Folder deletion is file-system bound and each pass is
    /// a single quick attempt, so a small fixed value keeps disk pressure modest.
    /// </summary>
    private const int MaxParallelCleanups = 4;

    public async Task<SweepPendingFeatureFoldersResponse> ExecuteAsync(SweepPendingFeatureFoldersRequest request, CancellationToken cancellationToken = default)
    {
        var storageRoot = request.FeatureStorageRoot ?? throw new ArgumentException("featureStorageRoot required");
        var response = new SweepPendingFeatureFoldersResponse();

        // Distinct folders are independent, so they are cleaned in parallel (bounded); counters are Interlocked.
        var folders = cleaner.FindMarkedFolders(storageRoot, request.ExcludeFeatureNames ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var removed = 0;
        var stillPending = 0;
        var refused = 0;

        await Parallel.ForEachAsync(
            folders,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelCleanups, CancellationToken = cancellationToken },
            async (folder, token) =>
            {
                var result = await cleaner.CleanupAsync(storageRoot, folder, request.WorkspaceName, featureName: null, retry: false, requireMarker: true, token);
                switch (result.Outcome)
                {
                    case FeatureFolderCleanupOutcome.Removed:
                        Interlocked.Increment(ref removed);
                        break;
                    case FeatureFolderCleanupOutcome.PendingDeletion:
                        Interlocked.Increment(ref stillPending);
                        break;
                    case FeatureFolderCleanupOutcome.NotMarked:
                        break;
                    default:
                        Interlocked.Increment(ref refused);
                        break;
                }
            });

        response.Removed = removed;
        response.StillPending = stillPending;
        response.Refused = refused;
        return response;
    }
}
