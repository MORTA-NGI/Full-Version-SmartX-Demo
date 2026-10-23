using SmartX.Core.Collections;
using SmartX.Core.Contracts;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Strongly typed store for one primitive (float / int / bool).
/// Pipeline: <c>TelemetryPacket&lt;T&gt;</c> → jagged staging rows (<c>T[][]</c>) → <see cref="TelemetryLedger{T}"/> (List&lt;T&gt;).
/// </summary>
public sealed class TypedTelemetryStore<T> where T : struct, IEquatable<T>
{
    private readonly JaggedStagingBuffer<TelemetryPacket<T>> _staging = new(rowCount: 8, rowSize: 128);
    private readonly TelemetryLedger<TelemetryPacket<T>> _ledger = new(capacity: 120_000);
    private readonly Lock _gate = new();

    public long Flushes { get; private set; }

    public void Add(in TelemetryPacket<T> packet)
    {
        lock (_gate)
        {
            if (_staging.Add(packet)) // returns true when all rows are full
            {
                _ledger.Absorb(_staging); // block-copy every row into the List<T>
                Flushes++;
            }
        }
    }

    /// <summary>A device backfill burst is staged as its own (jagged) row.</summary>
    public void AddBurst(TelemetryPacket<T>[] burst)
    {
        lock (_gate)
        {
            if (_staging.TryAppendRow(burst)) return;
            _ledger.Absorb(_staging);
            Flushes++;
            if (!_staging.TryAppendRow(burst)) _ledger.AppendRange(burst);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_staging.StagedCount == 0) return;
            _ledger.Absorb(_staging);
            Flushes++;
        }
    }

    public (int[] Rows, int RowSize, int LedgerCount, long Appended) Stats()
    {
        lock (_gate) return (_staging.RowFillSnapshot(), _staging.RowSize, _ledger.Count, _ledger.TotalAppended);
    }

    /// <summary>Most recent packets for one device, newest last (zero-copy span walk over the ledger tail).</summary>
    public List<TelemetryPacket<T>> RecentFor(string mac, int max, int scanWindow = 20_000)
    {
        lock (_gate)
        {
            var result = new List<TelemetryPacket<T>>(max);
            var tail = _ledger.Tail(scanWindow);
            for (int i = tail.Length - 1; i >= 0 && result.Count < max; i--)
            {
                if (string.Equals(tail[i].DeviceId, mac, StringComparison.Ordinal)) result.Add(tail[i]);
            }
            result.Reverse();
            return result;
        }
    }
}

/// <summary>Holds one typed store per supported primitive. No store ever sees an <c>object</c>.</summary>
public sealed class TelemetryHub
{
    public TypedTelemetryStore<float> Floats { get; } = new();
    public TypedTelemetryStore<int> Ints { get; } = new();
    public TypedTelemetryStore<bool> Bools { get; } = new();

    /// <summary>Unpacks the tagged-union envelope back into the right generic packet - no boxing anywhere.</summary>
    public void Store(in TelemetryEnvelope env)
    {
        switch (env.Kind)
        {
            case ValueKind.Float: Floats.Add(env.AsFloat()); break;
            case ValueKind.Int: Ints.Add(env.AsInt()); break;
            case ValueKind.Bool: Bools.Add(env.AsBool()); break;
        }
    }

    public PipelineStatsDto Stats()
    {
        var f = Floats.Stats();
        var i = Ints.Stats();
        var b = Bools.Stats();
        return new PipelineStatsDto(f.RowSize, f.Rows, i.Rows, b.Rows, f.LedgerCount, i.LedgerCount, b.LedgerCount,
            f.Appended + i.Appended + b.Appended, Floats.Flushes + Ints.Flushes + Bools.Flushes);
    }
}
