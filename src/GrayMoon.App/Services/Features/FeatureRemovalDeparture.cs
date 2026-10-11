using GrayMoon.Application.Features;
using Microsoft.AspNetCore.WebUtilities;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// Confirm-time leave for Remove Feature. Queues the background removal, starts a Workspace
/// status refresh that does not wait for that removal, then persists and returns the Workspace view.
/// Feature lifecycle is not consulted.
/// </summary>
public sealed class FeatureRemovalDeparture(
    IFeatureRemovalQueue queue,
    IWorkspaceFeatureContextResolver contextResolver,
    IWorkspaceSelectedFeatureContextService selectedContextService,
    IWorkspaceCheckoutRefresh checkoutRefresh) : IFeatureRemovalDeparture
{
    public async Task<FeatureRemovalLeave> ConfirmAsync(
        WorkspaceFeatureContextId featureContextId,
        RemoveFeatureOptions options,
        string currentUri,
        CancellationToken cancellationToken = default)
    {
        var info = await contextResolver.GetRequiredAsync(featureContextId, cancellationToken: cancellationToken);
        if (info.IsSpecialWorkspace)
            throw new InvalidOperationException("Cannot remove the special Workspace context.");

        if (!await queue.TryEnqueueAsync(featureContextId, options, cancellationToken))
            return FeatureRemovalLeave.AlreadyQueued();

        var workspaceId = info.WorkspaceId;
        var specialId = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        var workspace = await contextResolver.GetRequiredAsync(specialId, workspaceId, cancellationToken);
        await selectedContextService.SetSelectedAsync(workspaceId, specialId, cancellationToken);
        checkoutRefresh.Start(workspaceId, specialId);
        return FeatureRemovalLeave.ToWorkspace(workspace, WorkspacePath(currentUri));
    }

    /// <summary>
    /// The current page with <c>?context=</c> removed so the Workspace view opens.
    /// Other query parameters are kept.
    /// </summary>
    internal static string WorkspacePath(string currentUri)
    {
        var uri = new Uri(currentUri, UriKind.Absolute);
        var query = QueryHelpers.ParseQuery(uri.Query);
        var kept = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query)
        {
            if (string.Equals(pair.Key, "context", StringComparison.OrdinalIgnoreCase))
                continue;
            kept[pair.Key] = pair.Value.FirstOrDefault();
        }

        var path = uri.GetLeftPart(UriPartial.Path);
        return kept.Count == 0 ? path : QueryHelpers.AddQueryString(path, kept);
    }
}
