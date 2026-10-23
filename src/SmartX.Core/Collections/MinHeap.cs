namespace SmartX.Core.Collections;

/// <summary>
/// Array-backed binary min-heap used by Prim's MST algorithm (and Dijkstra).
/// Parent of i is (i-1)/2, children are 2i+1 and 2i+2. Push and Pop are O(log n), Peek is O(1).
/// </summary>
public sealed class MinHeap<TElement, TPriority> where TPriority : IComparable<TPriority>
{
    private (TElement Element, TPriority Priority)[] _items;

    public MinHeap(int capacity = 16) => _items = new (TElement, TPriority)[Math.Max(capacity, 4)];

    public int Count { get; private set; }

    public void Push(TElement element, TPriority priority)
    {
        if (Count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
        _items[Count] = (element, priority);
        SiftUp(Count++);
    }

    public bool TryPop(out TElement element, out TPriority priority)
    {
        if (Count == 0)
        {
            element = default!;
            priority = default!;
            return false;
        }
        (element, priority) = _items[0];
        _items[0] = _items[--Count];
        _items[Count] = default;
        if (Count > 0) SiftDown(0);
        return true;
    }

    public bool TryPeek(out TElement element, out TPriority priority)
    {
        if (Count == 0) { element = default!; priority = default!; return false; }
        (element, priority) = _items[0];
        return true;
    }

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            int parent = (i - 1) / 2;
            if (_items[i].Priority.CompareTo(_items[parent].Priority) >= 0) break;
            (_items[i], _items[parent]) = (_items[parent], _items[i]);
            i = parent;
        }
    }

    private void SiftDown(int i)
    {
        while (true)
        {
            int left = 2 * i + 1, right = left + 1, smallest = i;
            if (left < Count && _items[left].Priority.CompareTo(_items[smallest].Priority) < 0) smallest = left;
            if (right < Count && _items[right].Priority.CompareTo(_items[smallest].Priority) < 0) smallest = right;
            if (smallest == i) return;
            (_items[i], _items[smallest]) = (_items[smallest], _items[i]);
            i = smallest;
        }
    }
}
