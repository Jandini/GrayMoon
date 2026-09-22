using GrayMoon.Application.Features;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// Resolves navigation preference and query-string context for Workspace pages.
/// Never used as mutation/query execution authority by itself - pages pass the resolved id explicitly.
/// </summary>
public sealed class WorkspaceContextNavigationService(
    IWorkspaceFeatureContextResolver contextResolver,
    IWorkspaceSelectedFeatureContextService selectedContextService,
    NavigationManager navigation,
    ILogger<WorkspaceContextNavigationService> logger)
{
    public async Task<WorkspaceFeatureContextInfo> ResolveForPageAsync(
        int workspaceId,
        int? contextQuery,
        CancellationToken cancellationToken = default)
    {
        if (contextQuery is int q && q > 0)
        {
            var fromQuery = await TryResolveOwnedAsync(new WorkspaceFeatureContextId(q), workspaceId, cancellationToken);
            if (fromQuery is not null)
            {
                await selectedContextService.SetSelectedAsync(workspaceId, fromQuery.ContextId, cancellationToken);
                return fromQuery;
            }

            // Jumping workspaces keeps ?context= from the previous workspace (sidebar links and
            // history). That id is not valid here; drop it and use this workspace's own selection.
            logger.LogWarning(
                "Ignoring context {ContextId} on workspace {WorkspaceId}; it belongs to another workspace or is missing.",
                q, workspaceId);
            StripContextQuery();
        }

        var preferred = await selectedContextService.GetSelectedAsync(workspaceId, cancellationToken);
        if (preferred is WorkspaceFeatureContextId preferredId)
        {
            var info = await TryResolveOwnedAsync(preferredId, workspaceId, cancellationToken);
            if (info is not null)
            {
                if (!info.IsSpecialWorkspace)
                    CanonicalizeQuery(info.ContextId);
                return info;
            }
        }

        var special = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        var specialInfo = await contextResolver.GetRequiredAsync(special, workspaceId, cancellationToken);
        await selectedContextService.SetSelectedAsync(workspaceId, special, cancellationToken);
        return specialInfo;
    }

    private async Task<WorkspaceFeatureContextInfo?> TryResolveOwnedAsync(
        WorkspaceFeatureContextId contextId,
        int workspaceId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await contextResolver.GetRequiredAsync(contextId, workspaceId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void StripContextQuery()
    {
        var uri = new Uri(navigation.Uri);
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (!query.ContainsKey("context"))
            return;

        var kept = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query)
        {
            if (string.Equals(pair.Key, "context", StringComparison.OrdinalIgnoreCase))
                continue;
            kept[pair.Key] = pair.Value.FirstOrDefault();
        }

        var path = uri.GetLeftPart(UriPartial.Path);
        var target = kept.Count == 0 ? path : QueryHelpers.AddQueryString(path, kept);
        navigation.NavigateTo(target, replace: true);
    }

    public string AppendContextQuery(string relativePathWithoutQuery, WorkspaceFeatureContextId? contextId, bool isSpecialWorkspace)
    {
        if (contextId is null || isSpecialWorkspace)
            return relativePathWithoutQuery;
        var sep = relativePathWithoutQuery.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{relativePathWithoutQuery}{sep}context={contextId.Value.Value}";
    }

    public string CurrentContextQuerySuffix()
    {
        var uri = new Uri(navigation.Uri);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        if (query.TryGetValue("context", out var values)
            && int.TryParse(values.FirstOrDefault(), out var id)
            && id > 0)
            return $"?context={id}";
        return "";
    }

    private void CanonicalizeQuery(WorkspaceFeatureContextId contextId)
    {
        var path = new Uri(navigation.Uri).GetLeftPart(UriPartial.Path);
        navigation.NavigateTo($"{path}?context={contextId.Value}", replace: true);
    }
}
