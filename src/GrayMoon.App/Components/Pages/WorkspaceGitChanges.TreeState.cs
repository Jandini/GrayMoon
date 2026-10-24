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

    private string SelectionStorageKey(int contextId) => $"graymoon.gitChanges.selection.{WorkspaceId}.{contextId}";

    /// <summary>Serialises a remembered selection for localStorage (empty string = nothing selected).</summary>
    internal static string SerializeSelection(WorkspaceGitChangesSelectionMemory.Selection selection) =>
        JsonSerializer.Serialize(selection, TreeStateJson);

    /// <summary>Parses <see cref="SerializeSelection"/> output; null for empty, malformed or incomplete data.</summary>
    internal static WorkspaceGitChangesSelectionMemory.Selection? ParseSelection(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            var selection = JsonSerializer.Deserialize<WorkspaceGitChangesSelectionMemory.Selection>(raw, TreeStateJson);
            return selection is { FilePath: { Length: > 0 } } ? selection : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Remembers the selected file in the circuit-scoped memory (SPA navigation) and in browser localStorage
    /// (F5 / Ctrl+F5 destroy the circuit), per Workspace and context, like the tree expand state.
    /// </summary>
    private void RememberSelection(GitChangesTreeRow row)
    {
        var selection = new WorkspaceGitChangesSelectionMemory.Selection(
            row.WorkspaceRepositoryId, row.FilePath!, row.IsStagedSection);
        SelectionMemory.Set(WorkspaceId, selection);
        _ = WriteSelectionStorageAsync(SerializeSelection(selection));
    }

    /// <summary>Drops the remembered selection from both the circuit memory and localStorage.</summary>
    private void ForgetSelection()
    {
        SelectionMemory.Clear(WorkspaceId);
        _ = WriteSelectionStorageAsync(string.Empty);
    }

    private async Task WriteSelectionStorageAsync(string value)
    {
        if (_disposed || _selectedContextId is not { } ctxId)
            return;

        try
        {
            await Js.InvokeVoidAsync("graymoonStorageSet", SelectionStorageKey(ctxId.Value), value);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to persist Git Changes selection");
        }
    }

    /// <summary>Reads the selection stored before a reload. Best-effort; never throws.</summary>
    private async Task<WorkspaceGitChangesSelectionMemory.Selection?> LoadPersistedSelectionAsync()
    {
        if (_disposed || _selectedContextId is not { } ctxId)
            return null;

        try
        {
            var raw = await Js.InvokeAsync<string?>("graymoonStorageGet", SelectionStorageKey(ctxId.Value));
            return ParseSelection(raw);
        }
        catch (JSDisconnectedException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to read persisted Git Changes selection");
            return null;
        }
    }

    private static readonly JsonSerializerOptions TreeStateJson = new() { PropertyNameCaseInsensitive = true };
}
