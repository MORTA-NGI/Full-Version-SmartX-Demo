using SmartX.Core.Collections;
using SmartX.Core.Contracts;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Edge validation for incoming device packets, then hand-off to the right processing lane.
/// The method is generic over <typeparamref name="T"/> so float / int / bool packets travel through the same
/// code path as their own concrete types - nothing is converted to <c>object</c>.
/// </summary>
public sealed class IngestionGateway(DeviceRegistry registry, StreamProcessor processor)
{
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxBackfillAge = TimeSpan.FromDays(7);

    public (IngestAck? Ack, string? Error) Ingest<T>(
        TelemetryRequest<T> request,
        ValueKind expectedKind,
        Func<TelemetryPacket<T>, long, TelemetryEnvelope> wrap,
        PacketQuality quality = PacketQuality.Good) where T : struct, IEquatable<T>
    {
        if (!MacAddress.TryNormalize(request.Mac, out var mac)) return (null, "Invalid MAC address.");
        if (!registry.TryGet(mac, out var device)) return (null, $"Device {mac} is not registered with this gateway.");  // O(1)
        if (device.Metric.Kind != expectedKind)
            return (null, $"{device.DisplayName} publishes {device.Metric.Kind} values, not {expectedKind}.");
        if (!string.IsNullOrEmpty(request.Metric) && !string.Equals(request.Metric, device.Metric.Key, StringComparison.OrdinalIgnoreCase))
            return (null, $"{device.DisplayName} publishes '{device.Metric.Key}', not '{request.Metric}'.");

        double numeric = TelemetryMath.ToDouble(request.Value); // specialised per T, no boxing
        if (double.IsNaN(numeric) || double.IsInfinity(numeric)) return (null, "Value must be a finite number.");

        var now = DateTime.UtcNow;
        var ts = request.TimestampUtc?.ToUniversalTime() ?? now;
        if (ts > now + MaxClockSkew) return (null, "Timestamp is in the future - check the device RTC.");
        if (ts < now - MaxBackfillAge) return (null, "Timestamp is older than the 7-day backfill window.");

        var packet = new TelemetryPacket<T>(mac, device.Metric.Key, request.Value, ts, quality);
        var envelope = wrap(packet, processor.NextSequence());

        var severity = quality == PacketQuality.Backfilled ? Severity.Normal : device.Metric.Classify(numeric);
        var lane = processor.Enqueue(envelope, severity);
        return (new IngestAck(true, lane, severity, envelope.Sequence,
            lane == "PRIORITY" ? "Critical reading - routed to the priority lane." : "Queued for FIFO processing."), null);
    }

    /// <summary>
    /// Backfill upload from a device that was offline: several bursts of different lengths (a jagged
    /// <c>float[][]</c>). The rows are flattened in order into a <c>List&lt;float&gt;</c> and replayed as
    /// timestamped packets through the FIFO lane.
    /// </summary>
    public (BatchAck? Ack, string? Error) IngestBatch(BatchTelemetryRequest request)
    {
        if (!MacAddress.TryNormalize(request.Mac, out var mac) || !registry.TryGet(mac, out var device))
            return (null, "Unknown device.");
        if (device.Metric.Kind != ValueKind.Float) return (null, "Batch backfill is supported for float sensors.");
        if (request.Batches is null || request.Batches.Length == 0) return (null, "No batches supplied.");
        if (request.Batches.Length > 64 || request.Batches.Any(r => r is null || r.Length > 2048))
            return (null, "Too many rows (max 64) or a row longer than 2048 readings.");
        if (request.IntervalMs is < 100 or > 3_600_000) return (null, "IntervalMs must be between 100 and 3 600 000.");

        List<float> flat = JaggedStagingBuffer<float>.Flatten(request.Batches);
        var start = request.StartUtc.ToUniversalTime();
        for (int i = 0; i < flat.Count; i++)
        {
            var (ack, error) = Ingest(new TelemetryRequest<float>(mac, device.Metric.Key, flat[i], start.AddMilliseconds(i * (double)request.IntervalMs)),
                ValueKind.Float, static (p, s) => TelemetryEnvelope.From(p, s), PacketQuality.Backfilled);
            if (ack is null) return (null, $"Reading {i}: {error}");
        }

        var lengths = request.Batches.Select(r => r.Length).ToArray();
        return (new BatchAck(true, lengths.Length, flat.Count, lengths,
            $"Flattened {lengths.Length} jagged rows ({string.Join(" + ", lengths)}) into {flat.Count} readings."), null);
    }

    /// <summary>Injects a single critical reading to demonstrate the priority-queue bypass.</summary>
    public (IngestAck? Ack, string? Error) InjectCritical(string? macOrName)
    {
        var all = registry.Snapshot().Where(d => d.Kind != ValueKind.Bool).ToList();
        DeviceDto? target = string.IsNullOrWhiteSpace(macOrName)
            ? all.FirstOrDefault(d => d.DisplayName == "MOIST-B2") ?? all.FirstOrDefault()
            : all.FirstOrDefault(d => d.Mac.Equals(macOrName, StringComparison.OrdinalIgnoreCase) || d.DisplayName.Equals(macOrName, StringComparison.OrdinalIgnoreCase));
        if (target is null) return (null, "No numeric sensor found.");

        var def = MetricCatalog.Get(target.Metric);
        double v = def.CriticalLow > 0 ? def.CriticalLow * 0.6 : def.CriticalHigh * 1.15;
        return def.Kind == ValueKind.Int
            ? Ingest(new TelemetryRequest<int>(target.Mac, def.Key, (int)v), ValueKind.Int, static (p, s) => TelemetryEnvelope.From(p, s))
            : Ingest(new TelemetryRequest<float>(target.Mac, def.Key, (float)Math.Round(v, 1)), ValueKind.Float, static (p, s) => TelemetryEnvelope.From(p, s));
    }

    /// <summary>Floods the FIFO lane with realistic normal readings to build a backlog.</summary>
    public int Flood(int count)
    {
        count = Math.Clamp(count, 1, 20_000);
        var devices = registry.Snapshot().Where(d => d.Kind != ValueKind.Bool && d.LastValue is not null).ToList();
        if (devices.Count == 0) return 0;
        var rng = Random.Shared;
        for (int i = 0; i < count; i++)
        {
            var d = devices[rng.Next(devices.Count)];
            var def = MetricCatalog.Get(d.Metric);
            double v = Math.Clamp(d.LastValue!.Value + SignalModel.Noise(d.Metric) * SignalModel.Gaussian(rng) * 0.3,
                def.NormalMin, def.NormalMax);
            if (def.Kind == ValueKind.Int)
                Ingest(new TelemetryRequest<int>(d.Mac, def.Key, (int)v), ValueKind.Int, static (p, s) => TelemetryEnvelope.From(p, s), PacketQuality.Simulated);
            else
                Ingest(new TelemetryRequest<float>(d.Mac, def.Key, (float)v), ValueKind.Float, static (p, s) => TelemetryEnvelope.From(p, s), PacketQuality.Simulated);
        }
        return count;
    }
}
