using GrayMoon.App.Components.Features;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Ui;

/// <summary>
/// Recent Open-in tools for one Workspace, stored in the workspace database.
/// The feature selector paints from <see cref="TryGetCached"/> so a later page can show the
/// buttons with New Feature, then reads the database when this circuit has not loaded that Workspace.
/// </summary>
public sealed class WorkspaceOpenInRecentTools(IDbContextFactory<AppDbContext> dbContextFactory)
{
    private readonly Dictionary<int, IReadOnlyList<string>> _session = new();

    public async Task<IReadOnlyList<string>> GetAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        if (workspaceId <= 0)
            return [];

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await db.WorkspaceOpenInRecentTools.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId)
            .OrderBy(t => t.Position)
            .ThenBy(t => t.ToolId)
            .Select(t => t.ToolId)
            .ToListAsync(cancellationToken);

        var tools = new List<string>(stored.Count);
        foreach (var toolId in stored)
        {
            if (FeatureOpenInTools.IsRemembered(toolId) && !tools.Contains(toolId))
                tools.Add(toolId);
        }

        _session[workspaceId] = tools;
        return tools;
    }

    /// <summary>
    /// The list already known for this circuit, with no database read.
    /// </summary>
    public bool TryGetCached(int workspaceId, out IReadOnlyList<string> tools)
    {
        if (_session.TryGetValue(workspaceId, out var session))
        {
            tools = session;
            return true;
        }

        tools = [];
        return false;
    }

    public async Task<IReadOnlyList<string>> RecordAsync(
        int workspaceId,
        string toolId,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(workspaceId, cancellationToken);
        var next = FeatureOpenInTools.RecordUse(current, toolId);
        if (ReferenceEquals(next, current))
            return current;

        await SaveAsync(workspaceId, next, cancellationToken);
        return next;
    }

    public async Task<IReadOnlyList<string>> RemoveAsync(
        int workspaceId,
        string toolId,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(workspaceId, cancellationToken);
        var next = FeatureOpenInTools.Remove(current, toolId);
        if (ReferenceEquals(next, current))
            return current;

        await SaveAsync(workspaceId, next, cancellationToken);
        return next;
    }

    private async Task SaveAsync(int workspaceId, IReadOnlyList<string> tools, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.WorkspaceOpenInRecentTools
            .Where(t => t.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        var keep = new HashSet<string>(tools, StringComparer.Ordinal);
        foreach (var row in existing)
        {
            if (!keep.Contains(row.ToolId))
                db.WorkspaceOpenInRecentTools.Remove(row);
        }

        for (var i = 0; i < tools.Count; i++)
        {
            var toolId = tools[i];
            var row = existing.FirstOrDefault(r => string.Equals(r.ToolId, toolId, StringComparison.Ordinal));
            if (row is null)
            {
                db.WorkspaceOpenInRecentTools.Add(new WorkspaceOpenInRecentTool
                {
                    WorkspaceId = workspaceId,
                    ToolId = toolId,
                    Position = i,
                });
            }
            else
            {
                row.Position = i;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        _session[workspaceId] = tools.ToList();
    }
}
