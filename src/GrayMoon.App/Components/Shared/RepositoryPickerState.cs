namespace GrayMoon.App.Components.Shared;

/// <summary>One entry of a <see cref="RepositoryPicker"/>: the repository and the text it is listed and shown as.</summary>
public sealed record RepositoryPickerChoice(int RepositoryId, string DisplayName);

/// <summary>A selection the picker made: a repository, or "None" (<c>null</c>) when the picker allows it.</summary>
public readonly record struct RepositoryPickerSelection(int? RepositoryId);

/// <summary>
/// Open/filter/highlight state of the searchable repository picker, kept out of the component so it can be tested.
/// The rows of the open list are "None" (only when <see cref="AllowNone"/>) followed by the filtered choices; the
/// highlight is a row index into that list.
/// </summary>
public sealed class RepositoryPickerState
{
    public bool AllowNone { get; set; }

    public bool IsOpen { get; private set; }

    public string Filter { get; private set; } = string.Empty;

    public int HighlightIndex { get; private set; }

    private int NoneRows => AllowNone ? 1 : 0;

    /// <summary>Case-insensitive "contains" filter on display name; an empty term returns all, order preserved.</summary>
    public static IReadOnlyList<RepositoryPickerChoice> FilterChoices(IEnumerable<RepositoryPickerChoice> choices, string? term)
    {
        var trimmed = term?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return choices.ToList();
        return choices
            .Where(c => c.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Clamps a highlight move within <paramref name="rowCount"/> rows. No wrapping.</summary>
    public static int MoveHighlight(int currentIndex, int delta, int rowCount) =>
        Math.Clamp(currentIndex + delta, 0, Math.Max(rowCount - 1, 0));

    /// <summary>What the closed input shows: the selected repository, "None" when allowed, otherwise nothing (the placeholder).</summary>
    public static string ClosedText(IEnumerable<RepositoryPickerChoice> choices, int? selectedRepositoryId, bool allowNone, string noneLabel) =>
        choices.FirstOrDefault(c => c.RepositoryId == selectedRepositoryId)?.DisplayName
        ?? (allowNone ? noneLabel : string.Empty);

    /// <summary>
    /// Keeps a picker selection across a catalog refresh. A merged id follows
    /// <paramref name="mergedRepositoryIdMap"/> (the same rename reconciliation a fetch already returns).
    /// The selection is cleared when that repository is no longer in the catalog. Null ("None") stays null.
    /// </summary>
    public static int? ReconcileSelection(
        int? selectedRepositoryId,
        IReadOnlyDictionary<int, int>? mergedRepositoryIdMap,
        IEnumerable<int> availableRepositoryIds)
    {
        if (selectedRepositoryId is not int id)
            return null;

        if (mergedRepositoryIdMap is not null && mergedRepositoryIdMap.TryGetValue(id, out var canonical))
            id = canonical;

        foreach (var available in availableRepositoryIds)
        {
            if (available == id)
                return id;
        }

        return null;
    }

    public IReadOnlyList<RepositoryPickerChoice> Filtered(IReadOnlyList<RepositoryPickerChoice> choices) =>
        FilterChoices(choices, Filter);

    /// <summary>True when the row for <paramref name="filteredIndex"/> (a filtered choice) is highlighted.</summary>
    public bool IsChoiceHighlighted(int filteredIndex) => HighlightIndex == filteredIndex + NoneRows;

    public bool IsNoneHighlighted => AllowNone && HighlightIndex == 0;

    /// <summary>Opens with an empty filter (all choices) and the current selection highlighted.</summary>
    public void Open(IReadOnlyList<RepositoryPickerChoice> choices, int? selectedRepositoryId)
    {
        if (IsOpen)
            return;

        Filter = string.Empty;
        IsOpen = true;
        var selectedIndex = choices.ToList().FindIndex(c => c.RepositoryId == selectedRepositoryId);
        HighlightIndex = selectedIndex >= 0 ? selectedIndex + NoneRows : 0;
    }

    public void Close() => IsOpen = false;

    /// <summary>New filter text: highlights the first matching repository (or "None" when nothing matches).</summary>
    public void SetFilter(IReadOnlyList<RepositoryPickerChoice> choices, string? filter)
    {
        Filter = filter ?? string.Empty;
        HighlightIndex = AllowNone && Filtered(choices).Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// Applies a key while open. Escape closes without selecting; arrows move the highlight; Enter returns the
    /// highlighted row as the selection (and closes). Returns null when nothing was selected.
    /// </summary>
    public RepositoryPickerSelection? HandleKey(string key, IReadOnlyList<RepositoryPickerChoice> choices)
    {
        if (!IsOpen)
            return null;

        var filtered = Filtered(choices);
        switch (key)
        {
            case "Escape":
                IsOpen = false;
                return null;
            case "ArrowDown":
                HighlightIndex = MoveHighlight(HighlightIndex, 1, filtered.Count + NoneRows);
                return null;
            case "ArrowUp":
                HighlightIndex = MoveHighlight(HighlightIndex, -1, filtered.Count + NoneRows);
                return null;
            case "Enter":
            case "NumpadEnter":
                if (filtered.Count == 0 && (!AllowNone || !string.IsNullOrWhiteSpace(Filter)))
                    return null;
                if (IsNoneHighlighted)
                {
                    IsOpen = false;
                    return new RepositoryPickerSelection(null);
                }

                var index = HighlightIndex - NoneRows;
                if (index < 0 || index >= filtered.Count)
                    return null;
                IsOpen = false;
                return new RepositoryPickerSelection(filtered[index].RepositoryId);
            default:
                return null;
        }
    }
}
