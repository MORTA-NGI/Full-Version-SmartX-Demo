namespace SmartX.Core.Telemetry;

/// <summary>
/// A tagged union that lets packets of different <c>T</c> share ONE <c>Queue&lt;TelemetryEnvelope&gt;</c>
/// without boxing. Instead of storing the value as <c>object</c>, the struct has one inline slot per supported
/// primitive plus a <see cref="ValueKind"/> discriminator. The processor switches on <see cref="Kind"/> and
/// rebuilds the exact <see cref="TelemetryPacket{T}"/> it needs.
/// </summary>
public readonly struct TelemetryEnvelope
{
    private readonly float _float;
    private readonly int _int;
    private readonly bool _bool;

    private TelemetryEnvelope(string mac, string metric, ValueKind kind, float f, int i, bool b,
        DateTime timestampUtc, PacketQuality quality, long sequence)
    {
        Mac = mac;
        Metric = metric;
        Kind = kind;
        _float = f;
        _int = i;
        _bool = b;
        TimestampUtc = timestampUtc;
        Quality = quality;
        Sequence = sequence;
        EnqueuedUtc = DateTime.UtcNow;
    }

    public string Mac { get; }
    public string Metric { get; }
    public ValueKind Kind { get; }
    public DateTime TimestampUtc { get; }
    public DateTime EnqueuedUtc { get; }
    public PacketQuality Quality { get; }
    public long Sequence { get; }

    public static TelemetryEnvelope From(in TelemetryPacket<float> p, long seq) =>
        new(p.DeviceId, p.Metric, ValueKind.Float, p.Value, 0, false, p.TimestampUtc, p.Quality, seq);

    public static TelemetryEnvelope From(in TelemetryPacket<int> p, long seq) =>
        new(p.DeviceId, p.Metric, ValueKind.Int, 0, p.Value, false, p.TimestampUtc, p.Quality, seq);

    public static TelemetryEnvelope From(in TelemetryPacket<bool> p, long seq) =>
        new(p.DeviceId, p.Metric, ValueKind.Bool, 0, 0, p.Value, p.TimestampUtc, p.Quality, seq);

    public TelemetryPacket<float> AsFloat() => new(Mac, Metric, _float, TimestampUtc, Quality);
    public TelemetryPacket<int> AsInt() => new(Mac, Metric, _int, TimestampUtc, Quality);
    public TelemetryPacket<bool> AsBool() => new(Mac, Metric, _bool, TimestampUtc, Quality);

    /// <summary>Numeric projection for thresholds, charts and history.</summary>
    public double Numeric => Kind switch
    {
        ValueKind.Float => _float,
        ValueKind.Int => _int,
        _ => _bool ? 1 : 0
    };

    public string DisplayValue => Kind switch
    {
        ValueKind.Float => _float.ToString("0.##"),
        ValueKind.Int => _int.ToString(),
        _ => _bool ? "ON" : "OFF"
    };
}
