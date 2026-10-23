using System.Collections;
using System.Runtime.InteropServices;

namespace SmartX.Core.Collections;

/// <summary>
/// Custom bounded collection that sits at the end of the ingestion pipeline:
/// <c>jagged staging rows → TelemetryLedger&lt;T&gt; (backed by List&lt;T&gt;)</c>.
/// <list type="bullet">
/// <item>Appends whole spans (block copy) instead of single items.</item>
/// <item>Bounded: when <see cref="Capacity"/> is exceeded the oldest 10 % are dropped in a single
/// <c>RemoveRange</c> (amortised O(1) per item) - the gateway can never run out of memory.</item>
/// <item>Exposes the newest N items as a <see cref="ReadOnlySpan{T}"/> with no copying.</item>
/// </list>
/// </summary>
public sealed class TelemetryLedger<T> : IReadOnlyCollection<T> where T : struct
{
    private readonly List<T> _items;

    public TelemetryLedger(int capacity = 50_000)
    {
        if (capacity < 10) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _items = new List<T>(Math.Min(capacity, 4096));
    }

    public int Capacity { get; }

    public int Count => _items.Count;

    /// <summary>Total items ever appended (including trimmed ones).</summary>
    public long TotalAppended { get; private set; }

    public long TotalTrimmed { get; private set; }

    /// <summary>Gives the staging buffer direct access to the backing list for its span copy.</summary>
    internal List<T> Backing => _items;

    public void Append(in T item)
    {
        _items.Add(item);
        TotalAppended++;
        TrimIfNeeded();
    }

    public void AppendRange(ReadOnlySpan<T> items)
    {
        _items.AddRange(items);
        TotalAppended += items.Length;
        TrimIfNeeded();
    }

    /// <summary>Moves everything from a staging buffer into the ledger.</summary>
    public int Absorb(JaggedStagingBuffer<T> staging)
    {
        int moved = staging.FlushInto(_items);
        TotalAppended += moved;
        TrimIfNeeded();
        return moved;
    }

    /// <summary>Newest <paramref name="count"/> items, oldest first, as a zero-copy span.</summary>
    public ReadOnlySpan<T> Tail(int count)
    {
        var span = CollectionsMarshal.AsSpan(_items);
        return count >= span.Length ? span : span[^count..];
    }

    public T this[int index] => _items[index];

    private void TrimIfNeeded()
    {
        if (_items.Count <= Capacity) return;
        int excess = _items.Count - Capacity;
        int drop = Math.Max(excess, Capacity / 10);
        _items.RemoveRange(0, drop);
        TotalTrimmed += drop;
    }

    public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}
