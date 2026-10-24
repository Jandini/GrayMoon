using System.Text.Json;
using GrayMoon.App.Services.GitChanges;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    /// <summary>
    /// Persisted tree state for Summary ("large") mode. F5 / Ctrl+F5 destroy the circuit, so the expand state
    /// lives in browser localStorage, per Workspace and context. Holds the keys the tree considers collapsed
    /// (<c>c</c>) and every key whose default was already decided (<c>k</c>): a key in <c>k</c> but not in
    /// <c>c</c> is one the user opened. Keys not in <c>k</c> get today's defaults (new repos and folders open collapsed).
    /// </summary>
    private sealed record PersistedTreeState(List<string>? C, List<string>? K);

    private string TreeStateStorageKey(int contextId) => $"graymoon.gitChanges.tree.{WorkspaceId}.{contextId}";

    /// <summary>Applies the stored expand state over the freshly seeded defaults. Best-effort; never throws.</summary>
    private async Task RestoreTreeStateAsync(int contextId)
    {
        if (_disposed || _view is null)
            return;

        try
        {
            var raw = await Js.InvokeAsync<string?>("graymoonStorageGet", TreeStateStorageKey(contextId));
            if (string.IsNullOrWhiteSpace(raw))
                return;

            var state = JsonSerializer.Deserialize<PersistedTreeState>(raw, TreeStateJson);
            if (state is null)
                return;

            var collapsed = new HashSet<string>(state.C ?? [], StringComparer.Ordinal);
            foreach (var key in state.K ?? [])
            {
                if (collapsed.Contains(key))
                    _collapsedKeys.Add(key);
                else
                    _collapsedKeys.Remove(key);
                _summaryFoldersSeeded.Add(key);
            }

            foreach (var key in collapsed)
                _collapsedKeys.Add(key);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to restore Git Changes tree state");
        }
    }

    /// <summary>Re-fetches the first page of every restored expanded prefix so the tree paints as the user left it.</summary>
    private async Task HydrateRestoredTreeAsync()
    {
        if (_view is null)
            return;

        foreach (var repo in _view.Repositories.ToList())
        {
            if (_disposed)
                return;

            await RehydrateExpandedAsync(repo.WorkspaceRepositoryId);
        }
    }

    /// <summary>Writes the current expand state. Only Summary mode persists; Full mode defaults to expanded.</summary>
    private async Task PersistTreeStateAsync()
    {
        if (_disposed || _hydrationMode != GitChangesHydrationMode.Summary
            || _selectedContextId is not { } ctxId || _view is null)
        {
            return;
        }

        try
        {
            var known = new HashSet<string>(_summaryFoldersSeeded, StringComparer.Ordinal);
            foreach (var repo in _view.Repositories)
            {
                if (repo.StagedCount > 0)
                    known.Add($"staged/{repo.WorkspaceRepositoryId}");
                if (repo.ChangedCount > 0)
                    known.Add($"changed/{repo.WorkspaceRepositoryId}");
            }

            var json = JsonSerializer.Serialize(
                new PersistedTreeState([.. _collapsedKeys], [.. known]),
                TreeStateJson);
            await Js.InvokeVoidAsync("graymoonStorageSet", TreeStateStorageKey(ctxId.Value), json);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to persist Git Changes tree state");
        }
    }

    private static readonly JsonSerializerOptions TreeStateJson = new() { PropertyNameCaseInsensitive = true };
}
