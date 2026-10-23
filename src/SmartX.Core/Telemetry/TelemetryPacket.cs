using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SmartX.Core.Telemetry;

/// <summary>Quality flag carried with every packet (1 byte, keeps the struct small).</summary>
public enum PacketQuality : byte
{
    Good = 0,
    Backfilled = 1,
    Suspect = 2,
    Simulated = 3
}

/// <summary>
/// Reusable generic wrapper for a single telemetry reading.
/// <para>
/// Why this avoids boxing: <c>TelemetryPacket&lt;T&gt;</c> is a <b>struct</b> and <typeparamref name="T"/> is
/// constrained to <c>struct, IEquatable&lt;T&gt;</c>. The JIT therefore compiles a dedicated, specialised copy of
/// this type for every value type it is used with (<c>float</c> for soil moisture, <c>int</c> for wattage,
/// <c>bool</c> for valve states). The value is stored inline - never as <c>object</c> - and equality goes through
/// <c>IEquatable&lt;T&gt;.Equals</c> via a constrained call, so no heap allocation happens per reading.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct TelemetryPacket<T> : IEquatable<TelemetryPacket<T>>
    where T : struct, IEquatable<T>
{
    public TelemetryPacket(string deviceId, string metric, T value, DateTime timestampUtc,
        PacketQuality quality = PacketQuality.Good)
    {
        DeviceId = deviceId;
        Metric = metric;
        Value = value;
        TimestampUtc = timestampUtc;
        Quality = quality;
    }

    /// <summary>Normalised MAC address / unique identifier of the device that produced the reading.</summary>
    public string DeviceId { get; }

    /// <summary>Metric key from <see cref="MetricCatalog"/>, e.g. <c>soil_moisture</c>.</summary>
    public string Metric { get; }

    /// <summary>The strongly typed reading - stored inline, never boxed.</summary>
    public T Value { get; }

    public DateTime TimestampUtc { get; }

    public PacketQuality Quality { get; }

    public TelemetryPacket<T> WithValue(T value) => new(DeviceId, Metric, value, TimestampUtc, Quality);

    public bool Equals(TelemetryPacket<T> other) =>
        Value.Equals(other.Value) // constrained call on IEquatable<T> -> no boxing
        && TimestampUtc == other.TimestampUtc
        && string.Equals(DeviceId, other.DeviceId, StringComparison.Ordinal)
        && string.Equals(Metric, other.Metric, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is TelemetryPacket<T> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(DeviceId, Metric, Value, TimestampUtc);

    public static bool operator ==(TelemetryPacket<T> left, TelemetryPacket<T> right) => left.Equals(right);

    public static bool operator !=(TelemetryPacket<T> left, TelemetryPacket<T> right) => !left.Equals(right);

    public override string ToString() => $"{DeviceId}/{Metric}={Value} @ {TimestampUtc:HH:mm:ss.fff}";
}

/// <summary>
/// Operator overloads for <see cref="TelemetryPacket{T}"/> written as C# 14 <b>extension operators</b>.
/// Arithmetic is only offered when <typeparamref name="T"/> is numeric (<see cref="INumber{TSelf}"/>), so
/// <c>packetA + packetB</c> compiles for <c>float</c>/<c>int</c> packets and is rejected at compile-time for <c>bool</c>.
/// Generic math (<c>T.Zero</c>, <c>left.Value + right.Value</c>) is resolved statically per T - no boxing, no reflection.
/// </summary>
public static class TelemetryPacketOperators
{
    extension<T>(TelemetryPacket<T>) where T : struct, IEquatable<T>, INumber<T>
    {
        /// <summary>Aggregate two readings (e.g. total load of two smart meters).</summary>
        public static TelemetryPacket<T> operator +(TelemetryPacket<T> left, TelemetryPacket<T> right) =>
            new($"{left.DeviceId}+{right.DeviceId}", left.Metric, left.Value + right.Value,
                Latest(left.TimestampUtc, right.TimestampUtc), PacketQuality.Good);

        /// <summary>Delta between two readings (e.g. change in moisture since the last sample).</summary>
        public static TelemetryPacket<T> operator -(TelemetryPacket<T> left, TelemetryPacket<T> right) =>
            new($"{left.DeviceId}-{right.DeviceId}", left.Metric, left.Value - right.Value,
                Latest(left.TimestampUtc, right.TimestampUtc), PacketQuality.Good);

        public static bool operator >(TelemetryPacket<T> left, TelemetryPacket<T> right) => left.Value > right.Value;
        public static bool operator <(TelemetryPacket<T> left, TelemetryPacket<T> right) => left.Value < right.Value;
        public static bool operator >=(TelemetryPacket<T> left, TelemetryPacket<T> right) => left.Value >= right.Value;
        public static bool operator <=(TelemetryPacket<T> left, TelemetryPacket<T> right) => left.Value <= right.Value;
    }

    extension(TelemetryPacket<bool>)
    {
        /// <summary>Logical OR of two switch states - "is ANY valve open?"</summary>
        public static TelemetryPacket<bool> operator |(TelemetryPacket<bool> left, TelemetryPacket<bool> right) =>
            new($"{left.DeviceId}|{right.DeviceId}", left.Metric, left.Value | right.Value,
                Latest(left.TimestampUtc, right.TimestampUtc));

        /// <summary>Logical AND of two switch states - "are ALL pumps running?"</summary>
        public static TelemetryPacket<bool> operator &(TelemetryPacket<bool> left, TelemetryPacket<bool> right) =>
            new($"{left.DeviceId}&{right.DeviceId}", left.Metric, left.Value & right.Value,
                Latest(left.TimestampUtc, right.TimestampUtc));
    }

    private static DateTime Latest(DateTime a, DateTime b) => a >= b ? a : b;
}

/// <summary>Generic-math helpers that work over spans of packets without boxing.</summary>
public static class TelemetryMath
{
    public static T Sum<T>(ReadOnlySpan<TelemetryPacket<T>> packets) where T : struct, IEquatable<T>, INumber<T>
    {
        T total = T.Zero;
        foreach (ref readonly var p in packets)
        {
            total += p.Value;
        }
        return total;
    }

    public static double Mean<T>(ReadOnlySpan<TelemetryPacket<T>> packets) where T : struct, IEquatable<T>, INumber<T>
        => packets.IsEmpty ? 0 : double.CreateChecked(Sum(packets)) / packets.Length;

    public static (T Min, T Max) Range<T>(ReadOnlySpan<TelemetryPacket<T>> packets) where T : struct, IEquatable<T>, INumber<T>
    {
        if (packets.IsEmpty) return (T.Zero, T.Zero);
        T min = packets[0].Value, max = packets[0].Value;
        for (int i = 1; i < packets.Length; i++)
        {
            T v = packets[i].Value;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return (min, max);
    }

    /// <summary>Numeric projection used for charts / history. Bool maps to 0/1.</summary>
    public static double ToDouble<T>(T value) where T : struct, IEquatable<T>
    {
        // typeof(T) checks are JIT-time constants for value types, so only one branch survives per specialisation.
        // Unsafe.As reinterprets the value in place - there is no cast through object, therefore no boxing.
        if (typeof(T) == typeof(float)) return Unsafe.As<T, float>(ref value);
        if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref value);
        if (typeof(T) == typeof(bool)) return Unsafe.As<T, bool>(ref value) ? 1d : 0d;
        if (typeof(T) == typeof(double)) return Unsafe.As<T, double>(ref value);
        throw new NotSupportedException($"Unsupported telemetry type {typeof(T).Name}");
    }
}
