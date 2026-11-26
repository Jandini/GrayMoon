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

    /// <summary>
    /// <see cref="ResolveForPageAsync"/> for a page that must stay alive when its Workspace cannot be
    /// resolved. Returns null, and leaves the URL and the stored selection alone, when the page has no
    /// Workspace id yet (a parameter set without the route value arrives as 0) or the Workspace does not
    /// exist. An exception thrown from a page's parameter lifecycle ends the whole circuit ("Reload page").
    /// </summary>
    public async Task<WorkspaceFeatureContextInfo?> TryResolveForPageAsync(
        int workspaceId,
        int? contextQuery,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId <= 0)
        {
            logger.LogWarning(
                "Page has no Workspace id (got {WorkspaceId}, context query {ContextQuery}) at {Uri}; nothing resolved.",
                workspaceId, contextQuery, navigation.Uri);
            return null;
        }

        try
        {
            return await ResolveForPageAsync(workspaceId, contextQuery, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(
                ex,
                "Could not resolve a context for workspace {WorkspaceId} (context query {ContextQuery}) at {Uri}.",
                workspaceId, contextQuery, navigation.Uri);
            return null;
        }
    }

    /// <summary>
    /// Same precedence as <see cref="ResolveForPageAsync"/> (valid <c>?context=</c>, then the stored
    /// selection, then the special Workspace) without persisting the selection or rewriting the URL.
    /// For sidebar widgets that only need to know which context a workspace link will open.
    /// </summary>
    public async Task<WorkspaceFeatureContextId> PeekForPageAsync(
        int workspaceId,
        int? contextQuery,
        CancellationToken cancellationToken = default)
    {
        if (contextQuery is int q && q > 0
            && await TryResolveOwnedAsync(new WorkspaceFeatureContextId(q), workspaceId, cancellationToken) is { } fromQuery)
            return fromQuery.ContextId;

        var preferred = await selectedContextService.GetSelectedAsync(workspaceId, cancellationToken);
        if (preferred is WorkspaceFeatureContextId preferredId
            && await TryResolveOwnedAsync(preferredId, workspaceId, cancellationToken) is { } fromPreference)
            return fromPreference.ContextId;

        return await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken);
    }

    /// <summary>
    /// The context a <c>?context=</c> value names when it changed while the page stayed open (browser back or
    /// forward, an edited URL). A Feature id of this Workspace selects that Feature; no value, or an id that does not
    /// belong here, selects the special Workspace. Unlike <see cref="ResolveForPageAsync"/> it never revives the
    /// stored selection (a URL without the value means the Workspace, or the page would stay on the Feature the user
    /// just navigated away from) and never rewrites the URL. The result is stored as the selection.
    /// </summary>
    public async Task<WorkspaceFeatureContextInfo> ResolveQueryChangeAsync(
        int workspaceId,
        int? contextQuery,
        CancellationToken cancellationToken = default)
    {
        WorkspaceFeatureContextInfo? info = null;
        if (contextQuery is int q && q > 0)
        {
            info = await TryResolveOwnedAsync(new WorkspaceFeatureContextId(q), workspaceId, cancellationToken);
            if (info is null)
                logger.LogWarning("Ignoring context {ContextId} on workspace {WorkspaceId}; it belongs to another workspace or is missing.", q, workspaceId);
        }

        info ??= await contextResolver.GetRequiredAsync(
            await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken), workspaceId, cancellationToken);
        await selectedContextService.SetSelectedAsync(workspaceId, info.ContextId, cancellationToken);
        return info;
    }

    /// <summary>
    /// The context the selector handed to a page. The selector's list can be briefly behind (a Feature removed in
    /// another tab): a context that no longer belongs to the Workspace resolves to the special Workspace instead of
    /// throwing out of the page's event handler.
    /// </summary>
    public async Task<WorkspaceFeatureContextInfo> ResolveSelectionAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken = default)
        => await TryResolveOwnedAsync(contextId, workspaceId, cancellationToken)
           ?? await contextResolver.GetRequiredAsync(
               await contextResolver.GetOrCreateSpecialWorkspaceContextIdAsync(workspaceId, cancellationToken), workspaceId, cancellationToken);

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

    /// <summary>
    /// Entry href for opening a workspace page using the persisted navigation preference
    /// (Feature <c>?context=</c> when last selected; bare path for the special Workspace).
    /// </summary>
    public async Task<string> BuildWorkspaceEntryHrefAsync(
        int workspaceId,
        string relativePathWithoutQuery,
        CancellationToken cancellationToken = default)
    {
        var contextId = await PeekForPageAsync(workspaceId, contextQuery: null, cancellationToken);
        var info = await contextResolver.GetRequiredAsync(contextId, workspaceId, cancellationToken);
        return AppendContextQuery(relativePathWithoutQuery, contextId, info.IsSpecialWorkspace);
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
        var uri = new Uri(navigation.Uri);
        var query = QueryHelpers.ParseQuery(uri.Query);
        var kept = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query)
        {
            if (string.Equals(pair.Key, "context", StringComparison.OrdinalIgnoreCase))
                continue;
            kept[pair.Key] = pair.Value.FirstOrDefault();
        }

        kept["context"] = contextId.Value.ToString();
        var path = uri.GetLeftPart(UriPartial.Path);
        navigation.NavigateTo(QueryHelpers.AddQueryString(path, kept), replace: true);
    }
}
