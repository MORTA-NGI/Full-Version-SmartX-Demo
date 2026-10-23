namespace SmartX.Core.Telemetry;

/// <summary>
/// Deterministic "physics" for mock telemetry, shared by the API seeder (historical data) and the device
/// simulator (live data) so history and live readings line up seamlessly.
/// </summary>
public static class SignalModel
{
    private const double Day = 24 * 3600;

    /// <summary>Baseline value for a metric at a moment in time. <paramref name="seed"/> de-phases devices.</summary>
    public static double Baseline(string metric, DateTime utc, int seed)
    {
        // South African local time (UTC+2) drives day/night cycles.
        double local = (utc.AddHours(2) - utc.Date).TotalSeconds % Day;
        double hour = local / 3600.0;
        double phase = seed * 0.37;
        double daily = Math.Sin(2 * Math.PI * (hour - 9) / 24 + phase * 0.1);

        return metric switch
        {
            "soil_moisture" => 52 + 10 * Math.Sin(2 * Math.PI * local / (Day / 2) + phase) - 4 * daily,
            "temperature" => 22 + 6 * daily + seed % 3,
            "humidity" => 62 - 14 * daily + seed % 5,
            "power_w" => 650 + 90 * (seed % 7) + 520 * PeakLoad(hour),
            "grid_voltage" => 230 - 5 * PeakLoad(hour) + 1.5 * Math.Sin(phase),
            "pump_state" => hour is >= 6 and < 6.5 or >= 18 and < 18.5 ? 1 : 0,
            "valve_state" => hour is >= 6 and < 6.75 or >= 18 and < 18.75 ? 1 : 0,
            "relay_state" => hour is >= 22 or < 5 ? 0 : 1,
            _ => 0
        };
    }

    /// <summary>Gaussian-ish noise amplitude per metric.</summary>
    public static double Noise(string metric) => metric switch
    {
        "soil_moisture" => 1.2,
        "temperature" => 0.35,
        "humidity" => 1.4,
        "power_w" => 55,
        "grid_voltage" => 0.9,
        _ => 0
    };

    /// <summary>Morning (07:00) and evening (19:00) demand peaks typical of South African residential load.</summary>
    public static double PeakLoad(double hour) =>
        Math.Exp(-Math.Pow(hour - 7, 2) / 2) + 1.3 * Math.Exp(-Math.Pow(hour - 19, 2) / 3);

    public static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
