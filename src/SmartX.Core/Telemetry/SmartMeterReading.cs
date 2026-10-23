namespace SmartX.Core.Telemetry;

/// <summary>
/// A smart-meter reading with domain-specific operator overloading.
/// <code>
/// var meter3 = meter1 + meter2;   // aggregate load of two meters
/// var delta  = meterNow - meterThen; // consumption change
/// if (meter1 > meter2) ...          // compare instantaneous load
/// var perPhase = meter3 / 3;        // average across phases
/// </code>
/// It is a readonly struct so aggregating thousands of readings never allocates.
/// </summary>
public readonly struct SmartMeterReading : IEquatable<SmartMeterReading>, IComparable<SmartMeterReading>
{
    public SmartMeterReading(string meterId, int watts, double kilowattHours, DateTime timestampUtc, int sources = 1)
    {
        MeterId = meterId;
        Watts = watts;
        KilowattHours = kilowattHours;
        TimestampUtc = timestampUtc;
        Sources = sources;
    }

    public string MeterId { get; }

    /// <summary>Instantaneous load.</summary>
    public int Watts { get; }

    /// <summary>Accumulated energy.</summary>
    public double KilowattHours { get; }

    public DateTime TimestampUtc { get; }

    /// <summary>How many physical meters were aggregated into this value.</summary>
    public int Sources { get; }

    public static SmartMeterReading FromPacket(in TelemetryPacket<int> packet, double kwh = 0) =>
        new(packet.DeviceId, packet.Value, kwh, packet.TimestampUtc);

    /// <summary>Aggregate load: Meter3 = Meter1 + Meter2.</summary>
    public static SmartMeterReading operator +(SmartMeterReading a, SmartMeterReading b) =>
        new($"{a.MeterId} + {b.MeterId}", checked(a.Watts + b.Watts), a.KilowattHours + b.KilowattHours,
            a.TimestampUtc >= b.TimestampUtc ? a.TimestampUtc : b.TimestampUtc, a.Sources + b.Sources);

    /// <summary>Delta between two readings (positive = a is drawing more).</summary>
    public static SmartMeterReading operator -(SmartMeterReading a, SmartMeterReading b) =>
        new($"Δ({a.MeterId}, {b.MeterId})", a.Watts - b.Watts, a.KilowattHours - b.KilowattHours,
            a.TimestampUtc >= b.TimestampUtc ? a.TimestampUtc : b.TimestampUtc, a.Sources);

    /// <summary>Average an aggregate over N meters / phases.</summary>
    public static SmartMeterReading operator /(SmartMeterReading a, int divisor)
    {
        if (divisor <= 0) throw new DivideByZeroException("Divisor must be positive.");
        return new SmartMeterReading($"avg({a.MeterId})", a.Watts / divisor, a.KilowattHours / divisor, a.TimestampUtc, a.Sources);
    }

    /// <summary>Scale a reading (e.g. tariff multiplier or CT-clamp ratio).</summary>
    public static SmartMeterReading operator *(SmartMeterReading a, double factor) =>
        new(a.MeterId, (int)Math.Round(a.Watts * factor), a.KilowattHours * factor, a.TimestampUtc, a.Sources);

    public static bool operator >(SmartMeterReading a, SmartMeterReading b) => a.Watts > b.Watts;
    public static bool operator <(SmartMeterReading a, SmartMeterReading b) => a.Watts < b.Watts;
    public static bool operator >=(SmartMeterReading a, SmartMeterReading b) => a.Watts >= b.Watts;
    public static bool operator <=(SmartMeterReading a, SmartMeterReading b) => a.Watts <= b.Watts;
    public static bool operator ==(SmartMeterReading a, SmartMeterReading b) => a.Equals(b);
    public static bool operator !=(SmartMeterReading a, SmartMeterReading b) => !a.Equals(b);

    /// <summary>Is this load above a breaker limit? <c>if (meter &gt; 3500) ...</c></summary>
    public static bool operator >(SmartMeterReading a, int watts) => a.Watts > watts;
    public static bool operator <(SmartMeterReading a, int watts) => a.Watts < watts;

    public int CompareTo(SmartMeterReading other) => Watts.CompareTo(other.Watts);

    public bool Equals(SmartMeterReading other) =>
        Watts == other.Watts && KilowattHours.Equals(other.KilowattHours) && MeterId == other.MeterId;

    public override bool Equals(object? obj) => obj is SmartMeterReading r && Equals(r);

    public override int GetHashCode() => HashCode.Combine(MeterId, Watts, KilowattHours);

    public override string ToString() => $"{MeterId}: {Watts} W ({KilowattHours:F2} kWh)";
}

/// <summary>
/// Environmental sample (temperature, humidity, soil moisture) with overloads that make averaging and
/// delta-detection read naturally: <c>var avg = (s1 + s2 + s3) / 3; var drift = latest - baseline;</c>
/// </summary>
public readonly struct EnvironmentalSample : IEquatable<EnvironmentalSample>
{
    public EnvironmentalSample(float temperatureC, float humidityPct, float soilMoisturePct, int count = 1)
    {
        TemperatureC = temperatureC;
        HumidityPct = humidityPct;
        SoilMoisturePct = soilMoisturePct;
        Count = count;
    }

    public float TemperatureC { get; }
    public float HumidityPct { get; }
    public float SoilMoisturePct { get; }
    public int Count { get; }

    public static EnvironmentalSample operator +(EnvironmentalSample a, EnvironmentalSample b) =>
        new(a.TemperatureC + b.TemperatureC, a.HumidityPct + b.HumidityPct, a.SoilMoisturePct + b.SoilMoisturePct, a.Count + b.Count);

    public static EnvironmentalSample operator -(EnvironmentalSample a, EnvironmentalSample b) =>
        new(a.TemperatureC - b.TemperatureC, a.HumidityPct - b.HumidityPct, a.SoilMoisturePct - b.SoilMoisturePct, 1);

    public static EnvironmentalSample operator /(EnvironmentalSample a, int n) =>
        n <= 0 ? throw new DivideByZeroException() : new(a.TemperatureC / n, a.HumidityPct / n, a.SoilMoisturePct / n, 1);

    /// <summary>"Drier than" comparison used by irrigation logic.</summary>
    public static bool operator <(EnvironmentalSample a, EnvironmentalSample b) => a.SoilMoisturePct < b.SoilMoisturePct;
    public static bool operator >(EnvironmentalSample a, EnvironmentalSample b) => a.SoilMoisturePct > b.SoilMoisturePct;

    public bool Equals(EnvironmentalSample other) =>
        TemperatureC.Equals(other.TemperatureC) && HumidityPct.Equals(other.HumidityPct) && SoilMoisturePct.Equals(other.SoilMoisturePct);

    public override bool Equals(object? obj) => obj is EnvironmentalSample s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(TemperatureC, HumidityPct, SoilMoisturePct);
    public static bool operator ==(EnvironmentalSample a, EnvironmentalSample b) => a.Equals(b);
    public static bool operator !=(EnvironmentalSample a, EnvironmentalSample b) => !a.Equals(b);
}
