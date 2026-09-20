using GrayMoon.Application.Features;
using Microsoft.AspNetCore.Components;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// Resolves navigation preference and query-string context for Workspace pages.
/// Never used as mutation/query execution authority by itself - pages pass the resolved id explicitly.
/// </summary>
public sealed class WorkspaceContextNavigationService(
    IWorkspaceFeatureContextResolver contextResolver,
    IWorkspaceSelectedFeatureContextService selectedContextService,
    NavigationManager navigation)
{
    public async Task<WorkspaceFeatureContextInfo> ResolveForPageAsync(
        int workspaceId,
        int? contextQuery,
        CancellationToken cancellationToken = default)
    {
        if (contextQuery is int q && q > 0)
        {
            var info = await contextResolver.GetRequiredAsync(new WorkspaceFeatureContextId(q), workspaceId, cancellationToken);
            await selectedContextService.SetSelectedAsync(workspaceId, info.ContextId, cancellationToken);
            return info;
        }

        var preferred = await selectedContextService.GetSelectedAsync(workspaceId, cancellationToken);
        if (preferred is WorkspaceFeatureContextId preferredId)
        {
            var info = await contextResolver.GetRequiredAsync(preferredId, workspaceId, cancellationToken);
            if (!info.IsSpecialWorkspace)
                CanonicalizeQuery(info.ContextId);
            return info;
        }

        var special = await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
        return await contextResolver.GetRequiredAsync(special, workspaceId, cancellationToken);
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
