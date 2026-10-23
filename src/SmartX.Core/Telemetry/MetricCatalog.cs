using SmartX.Core.Devices;

namespace SmartX.Core.Telemetry;

/// <summary>The primitive type a metric is transmitted as.</summary>
public enum ValueKind : byte
{
    Float = 0,
    Int = 1,
    Bool = 2
}

public enum Severity : byte
{
    Normal = 0,
    Warning = 1,
    Critical = 2
}

/// <summary>Describes one metric an ESP32 can publish, including its healthy and critical bands.</summary>
public sealed record MetricDefinition(
    string Key,
    string Label,
    SensorCategory Category,
    ValueKind Kind,
    string Unit,
    double NormalMin,
    double NormalMax,
    double CriticalLow,
    double CriticalHigh)
{
    public Severity Classify(double value)
    {
        if (Kind == ValueKind.Bool) return Severity.Normal;
        if (value <= CriticalLow || value >= CriticalHigh) return Severity.Critical;
        if (value < NormalMin || value > NormalMax) return Severity.Warning;
        return Severity.Normal;
    }
}

/// <summary>Catalogue of metrics supported by the gateway (O(1) lookup by key).</summary>
public static class MetricCatalog
{
    private static readonly Dictionary<string, MetricDefinition> Definitions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["soil_moisture"] = new("soil_moisture", "Soil moisture", SensorCategory.Environmental, ValueKind.Float, "%", 25, 80, 15, 98),
        ["temperature"] = new("temperature", "Air temperature", SensorCategory.Environmental, ValueKind.Float, "°C", 15, 32, 5, 42),
        ["humidity"] = new("humidity", "Relative humidity", SensorCategory.Environmental, ValueKind.Float, "%RH", 35, 85, 15, 98),
        ["power_w"] = new("power_w", "Active power", SensorCategory.PowerConsumption, ValueKind.Int, "W", 0, 2500, -1, 3500),
        ["grid_voltage"] = new("grid_voltage", "Grid voltage", SensorCategory.PowerConsumption, ValueKind.Float, "V", 216, 244, 190, 260),
        ["pump_state"] = new("pump_state", "Water pump", SensorCategory.Actuator, ValueKind.Bool, "on/off", 0, 1, -1, 2),
        ["valve_state"] = new("valve_state", "Irrigation valve", SensorCategory.Actuator, ValueKind.Bool, "open/closed", 0, 1, -1, 2),
        ["relay_state"] = new("relay_state", "Smart relay", SensorCategory.Actuator, ValueKind.Bool, "on/off", 0, 1, -1, 2),
    };

    public static IReadOnlyCollection<MetricDefinition> All => Definitions.Values;

    public static bool TryGet(string key, out MetricDefinition definition) =>
        Definitions.TryGetValue(key, out definition!);

    public static MetricDefinition Get(string key) =>
        Definitions.TryGetValue(key, out var d) ? d : throw new KeyNotFoundException($"Unknown metric '{key}'.");

    public static IEnumerable<MetricDefinition> ForCategory(SensorCategory category) =>
        Definitions.Values.Where(d => d.Category == category);
}
