using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace GrayMoon.App.Components.Shared;

/// <summary>Flat virtual-table state: full scrollbar height from index count, hydrate only the visible window.</summary>
public sealed class VirtualTableScrollState<TItem> : IAsyncDisposable
{
    public const double DefaultRowHeightPx = 48;
    public const int DefaultOverscanSlots = 8;
    public const int DefaultInitialViewportSlots = 40;

    private readonly List<int> _ids = new();
    private readonly Dictionary<int, TItem> _itemsById = new();
    private IDisposable? _dotNetRef;
    private bool _attached;

    public double RowHeightPx { get; init; } = DefaultRowHeightPx;
    public int OverscanSlots { get; init; } = DefaultOverscanSlots;
    public int InitialViewportSlots { get; init; } = DefaultInitialViewportSlots;

    public int VisibleStart { get; private set; }
    public int VisibleEnd { get; private set; } = -1;
    public double TopSpacerPx { get; private set; }
    public double BottomSpacerPx { get; private set; }
    public bool IsAttached => _attached;

    public int Count => _ids.Count;
    public double TotalHeightPx => _ids.Count * RowHeightPx;
    public IReadOnlyList<int> Ids => _ids;

    public void SetIndex(IReadOnlyList<int> ids)
    {
        _ids.Clear();
        _ids.AddRange(ids);
        _itemsById.Clear();
        var end = Math.Min(_ids.Count - 1, InitialViewportSlots - 1);
        UpdateVisibleRange(0, Math.Max(-1, end));
        _attached = false;
    }

    public void Clear()
    {
        _ids.Clear();
        _itemsById.Clear();
        VisibleStart = 0;
        VisibleEnd = -1;
        TopSpacerPx = 0;
        BottomSpacerPx = 0;
        _attached = false;
    }

    public void CacheItems(IEnumerable<(int Id, TItem Item)> items)
    {
        foreach (var (id, item) in items)
        {
            _itemsById[id] = item;
        }
    }

    public bool TryGetItem(int id, out TItem item) => _itemsById.TryGetValue(id, out item!);

    public IReadOnlyList<int> GetMissingIds(int start, int end)
    {
        if (_ids.Count == 0 || end < start)
        {
            return Array.Empty<int>();
        }

        start = Math.Clamp(start, 0, _ids.Count - 1);
        end = Math.Clamp(end, start, _ids.Count - 1);
        var missing = new List<int>();
        for (var i = start; i <= end; i++)
        {
            var id = _ids[i];
            if (!_itemsById.ContainsKey(id))
            {
                missing.Add(id);
            }
        }

        return missing;
    }

    public void UpdateVisibleRange(int start, int end)
    {
        if (_ids.Count == 0)
        {
            VisibleStart = 0;
            VisibleEnd = -1;
            TopSpacerPx = 0;
            BottomSpacerPx = 0;
            return;
        }

        start = Math.Clamp(start, 0, _ids.Count - 1);
        end = Math.Clamp(end, start, _ids.Count - 1);
        VisibleStart = start;
        VisibleEnd = end;
        TopSpacerPx = start * RowHeightPx;
        BottomSpacerPx = (_ids.Count - end - 1) * RowHeightPx;
    }

    public (int Start, int End) ComputeRange(double scrollTop, double clientHeight)
    {
        if (_ids.Count == 0)
        {
            return (0, -1);
        }

        var overscanPx = OverscanSlots * RowHeightPx;
        var rangeStart = Math.Max(0, scrollTop - overscanPx);
        var rangeEnd = scrollTop + clientHeight + overscanPx;
        var start = (int)(rangeStart / RowHeightPx);
        var end = (int)(rangeEnd / RowHeightPx);
        start = Math.Clamp(start, 0, _ids.Count - 1);
        end = Math.Clamp(end, start, _ids.Count - 1);
        return (start, end);
    }

    public IEnumerable<(int Id, TItem? Item)> VisibleRows
    {
        get
        {
            if (_ids.Count == 0 || VisibleEnd < VisibleStart)
            {
                yield break;
            }

            for (var i = VisibleStart; i <= VisibleEnd && i < _ids.Count; i++)
            {
                var id = _ids[i];
                _itemsById.TryGetValue(id, out var item);
                yield return (id, item);
            }
        }
    }

    private ElementReference _scrollElement;

    public async Task AttachAsync<THost>(IJSRuntime js, THost host, ElementReference scrollElement)
        where THost : class
    {
        if (_attached || _ids.Count == 0)
        {
            return;
        }

        try
        {
            _scrollElement = scrollElement;
            _dotNetRef?.Dispose();
            var typedRef = DotNetObjectReference.Create(host);
            _dotNetRef = typedRef;
            await js.InvokeVoidAsync("grayMoonVirtualScroll.attach", _scrollElement, typedRef, TotalHeightPx);
            _attached = true;
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async Task DetachAsync(IJSRuntime js)
    {
        if (!_attached)
        {
            return;
        }

        try
        {
            await js.InvokeVoidAsync("grayMoonVirtualScroll.detach", _scrollElement);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        _attached = false;
    }

    public async Task SetTotalHeightAsync(IJSRuntime js)
    {
        if (!_attached)
        {
            return;
        }

        try
        {
            await js.InvokeVoidAsync("grayMoonVirtualScroll.setTotalHeight", _scrollElement, TotalHeightPx);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _dotNetRef?.Dispose();
        _dotNetRef = null;
        _attached = false;
    }
}
