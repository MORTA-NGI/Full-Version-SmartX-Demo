namespace SmartX.Core.Collections;

/// <summary>
/// Stages raw telemetry in a <b>jagged array</b> (<c>T[][]</c>) before it is moved into a generic list.
/// <para>
/// Layout: each row is one sequential "batch" (a burst of readings in arrival order). Rows are allocated
/// once and then <b>reused</b> after every flush, so steady-state ingestion produces zero garbage - important on
/// a memory-constrained gateway. Rows can also be appended whole (e.g. a device uploading an offline backfill
/// burst of a different length), which is exactly what a jagged array is good at: every row can have its own length.
/// </para>
/// <code>
/// rows ─┬─ [0] ■■■■■■■■■■■■■■■■ (full, 256)
///       ├─ [1] ■■■■■■■■■■■■■■■■ (full, 256)
///       ├─ [2] ■■■■■■□□□□□□□□□□ (current row, 6 used)
///       └─ [3] (not yet allocated)
/// </code>
/// </summary>
public sealed class JaggedStagingBuffer<T> where T : struct
{
    private readonly T[][] _rows;
    private readonly int[] _rowFill;
    private readonly int _rowSize;
    private int _currentRow;

    public JaggedStagingBuffer(int rowCount = 8, int rowSize = 256)
    {
        if (rowCount <= 0) throw new ArgumentOutOfRangeException(nameof(rowCount));
        if (rowSize <= 0) throw new ArgumentOutOfRangeException(nameof(rowSize));
        _rows = new T[rowCount][];
        _rowFill = new int[rowCount];
        _rowSize = rowSize;
    }

    public int RowCount => _rows.Length;
    public int RowSize => _rowSize;
    public int ActiveRow => _currentRow;

    /// <summary>Total readings currently staged across all rows.</summary>
    public int StagedCount
    {
        get
        {
            int total = 0;
            for (int i = 0; i < _rowFill.Length; i++) total += _rowFill[i];
            return total;
        }
    }

    /// <summary>True once every row is full - the owner should flush.</summary>
    public bool IsFull => _currentRow >= _rows.Length;

    /// <summary>Per-row fill levels (for the dashboard's pipeline visual).</summary>
    public int[] RowFillSnapshot() => (int[])_rowFill.Clone();

    /// <summary>Stage one reading. Returns true when the buffer became full and must be flushed.</summary>
    public bool Add(in T item)
    {
        if (IsFull) throw new InvalidOperationException("Staging buffer is full - flush before adding.");

        var row = _rows[_currentRow] ??= new T[_rowSize]; // lazy allocate, then reuse forever
        row[_rowFill[_currentRow]++] = item;

        if (_rowFill[_currentRow] == row.Length)
        {
            _currentRow++;
        }
        return IsFull;
    }

    /// <summary>
    /// Append a complete batch as its own row (jagged: may be shorter or longer than <see cref="RowSize"/>).
    /// Returns false when there is no free row - caller flushes and retries.
    /// </summary>
    public bool TryAppendRow(T[] batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Length == 0) return true;
        // Skip past a partially filled row so the batch keeps its own sequential row.
        int target = _currentRow < _rows.Length && _rowFill[_currentRow] == 0 ? _currentRow : _currentRow + 1;
        if (target >= _rows.Length) return false;

        _rows[target] = batch;                   // row now has the batch's own length -> jagged
        _rowFill[target] = batch.Length;
        _currentRow = target + 1;
        return true;
    }

    /// <summary>
    /// Move every staged reading, in arrival order, into <paramref name="target"/> and reset the buffer.
    /// Uses span-based <c>AddRange</c> so each row is block-copied rather than added item by item.
    /// </summary>
    public int FlushInto(List<T> target)
    {
        int moved = StagedCount;
        if (moved == 0) return 0;
        target.EnsureCapacity(target.Count + moved);

        for (int r = 0; r < _rows.Length; r++)
        {
            int fill = _rowFill[r];
            if (fill == 0) continue;
            target.AddRange(new ReadOnlySpan<T>(_rows[r], 0, fill));
            _rowFill[r] = 0;
            if (_rows[r].Length != _rowSize)
            {
                _rows[r] = null!; // drop the foreign (backfill) row; a standard row will be re-allocated on demand
            }
        }
        _currentRow = 0;
        return moved;
    }

    /// <summary>Utility used by the batch-ingest endpoint: flatten an incoming jagged payload in order.</summary>
    public static List<TItem> Flatten<TItem>(TItem[][] jagged)
    {
        int total = 0;
        foreach (var row in jagged) total += row?.Length ?? 0;
        var list = new List<TItem>(total);
        foreach (var row in jagged)
        {
            if (row is { Length: > 0 }) list.AddRange(row);
        }
        return list;
    }
}
