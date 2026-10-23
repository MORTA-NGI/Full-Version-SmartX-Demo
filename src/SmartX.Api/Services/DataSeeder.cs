using System.Diagnostics;
using Microsoft.Extensions.Options;
using SmartX.Core.Contracts;
using SmartX.Core.Deployment;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Heavily seeds the gateway on start-up so every data structure is exercised under load:
/// deployment tree, ~35 registered ESP32 sensors, ~48 h of history per sensor (≈100k readings into the AVL
/// trees and typed ledgers), learned operator patterns for the predictive engine and historical issues.
/// </summary>
public sealed class DataSeeder(
    DeploymentService deployment,
    RegistrationService registration,
    DeviceRegistry registry,
    TelemetryHub hub,
    HistoryService history,
    AnomalyDetector anomalies,
    InsightEngine insights,
    EngagementService engagement,
    IOptions<GatewayOptions> options,
    ILogger<DataSeeder> logger)
{
    private static readonly (string Name, string Metric, string Node)[] Sensors =
    [
        // ── Facility A: hydroponic farm ──
        ("MOIST-A1", "soil_moisture", "N-A1A-01"), ("TEMP-A1", "temperature", "N-A1A-01"),
        ("MOIST-A2", "soil_moisture", "N-A1A-02"), ("VALVE-A", "valve_state", "N-A1A-02"),
        ("MOIST-B1", "soil_moisture", "N-A1B-01"), ("HUM-B1", "humidity", "N-A1B-01"),
        ("MOIST-B2", "soil_moisture", "N-A1B-02"), ("VALVE-B", "valve_state", "N-A1B-02"),
        ("MOIST-C1", "soil_moisture", "N-A1C-01"), ("TEMP-C1", "temperature", "N-A1C-01"),
        ("HUM-C1", "humidity", "N-A1C-02"), ("VALVE-C", "valve_state", "N-A1C-02"),
        ("PUMP-A", "pump_state", "N-A2A-01"), ("POWER-PUMPS", "power_w", "N-A2A-01"),
        ("PUMP-B", "pump_state", "N-A2A-02"), ("VOLT-PR", "grid_voltage", "N-A2A-02"),
        ("TEMP-RES", "temperature", "N-A2B-01"), ("RELAY-UV", "relay_state", "N-A2B-01"),
        // ── Facility B: smart-grid sub-station ──
        ("METER-L1", "power_w", "N-B1A-01"), ("METER-L2", "power_w", "N-B1A-01"),
        ("METER-L3", "power_w", "N-B1A-02"), ("VOLT-MAIN", "grid_voltage", "N-B1A-02"),
        ("METER-GEN", "power_w", "N-B1B-01"), ("RELAY-GEN", "relay_state", "N-B1B-01"),
        ("METER-BLK1A", "power_w", "N-B2A-01"), ("RELAY-BLK1", "relay_state", "N-B2A-01"),
        ("METER-BLK1B", "power_w", "N-B2A-02"), ("TEMP-DB1", "temperature", "N-B2A-02"),
        ("METER-BLK2", "power_w", "N-B2B-01"), ("RELAY-BLK2", "relay_state", "N-B2B-01"),
        ("VOLT-BLK2", "grid_voltage", "N-B2B-02"), ("HUM-DB2", "humidity", "N-B2B-02"),
    ];

    public void Seed()
    {
        var sw = Stopwatch.StartNew();
        SeedDeployment();
        var rng = new Random(2026);
        var now = DateTime.UtcNow;

        // Register through the same validated path the UI uses.
        foreach (var (name, metric, node) in Sensors)
        {
            var def = MetricCatalog.Get(metric);
            var (result, errors) = registration.Register(new RegisterSensorRequest
            {
                Mac = MacAddress.Generate(rng),
                DisplayName = name,
                Category = def.Category,
                Metric = metric,
                NodeId = node
            }, now.AddDays(-30));
            if (result is null) logger.LogWarning("Seed registration failed for {Name}: {Errors}", name, string.Join("; ", errors.SelectMany(e => e.Value)));
        }

        long readings = SeedHistory(now, rng);
        SeedPatterns(now);
        SeedIssues(now);
        hub.Floats.Flush();
        hub.Ints.Flush();
        hub.Bools.Flush();

        logger.LogInformation("Seeded {Devices} devices and {Readings:N0} historical readings in {Ms} ms.",
            registry.Count, readings, sw.ElapsedMilliseconds);
    }

    private void SeedDeployment()
    {
        DeploymentNode N(string id, string name, int w = 35) => new() { Id = id, Name = name, Level = DeploymentLevel.Node, PowerWatts = w };
        DeploymentNode S(string id, string name, int cap, params DeploymentNode[] kids) =>
            new() { Id = id, Name = name, Level = DeploymentLevel.SubZone, PowerWatts = cap, Children = [.. kids] };
        DeploymentNode Z(string id, string name, int cap, params DeploymentNode[] kids) =>
            new() { Id = id, Name = name, Level = DeploymentLevel.Zone, PowerWatts = cap, Children = [.. kids] };

        deployment.AddFacility(new DeploymentNode
        {
            Id = "FAC-A", Name = "Facility A · Hydroponic Farm (Stellenbosch)", Level = DeploymentLevel.Facility, PowerWatts = 2000,
            Children =
            [
                Z("Z-A1", "Zone 1 · Grow Hall", 600,
                    S("SZ-A1A", "Sub-Zone A", 120, N("N-A1A-01", "Node A-01"), N("N-A1A-02", "Node A-02")),
                    S("SZ-A1B", "Sub-Zone B", 120, N("N-A1B-01", "Node B-01"), N("N-A1B-02", "Node B-02")),
                    S("SZ-A1C", "Sub-Zone C", 120, N("N-A1C-01", "Node C-01"), N("N-A1C-02", "Node C-02"))),
                Z("Z-A2", "Zone 2 · Nutrient Plant Room", 400,
                    S("SZ-A2A", "Pump Room", 150, N("N-A2A-01", "Node P-01", 60), N("N-A2A-02", "Node P-02", 60)),
                    S("SZ-A2B", "Reservoir", 100, N("N-A2B-01", "Node R-01", 45)))
            ]
        });

        deployment.AddFacility(new DeploymentNode
        {
            Id = "FAC-B", Name = "Facility B · Smart-Grid Sub-station (Midrand)", Level = DeploymentLevel.Facility, PowerWatts = 1500,
            Children =
            [
                Z("Z-B1", "Zone 1 · Main DB Room", 500,
                    S("SZ-B1A", "Phases L1-L3", 120, N("N-B1A-01", "Node M-01"), N("N-B1A-02", "Node M-02")),
                    S("SZ-B1B", "Backup Generator", 80, N("N-B1B-01", "Node G-01"))),
                Z("Z-B2", "Zone 2 · Residential Feeders", 500,
                    S("SZ-B2A", "Block 1", 120, N("N-B2A-01", "Node F-01"), N("N-B2A-02", "Node F-02")),
                    S("SZ-B2B", "Block 2", 120, N("N-B2B-01", "Node F-03"), N("N-B2B-02", "Node F-04")))
            ]
        });
    }

    private long SeedHistory(DateTime now, Random rng)
    {
        int hours = options.Value.SeedHistoryHours;
        int step = Math.Max(5, options.Value.SeedIntervalSeconds);
        var start = now.AddHours(-hours);
        long total = 0;
        int seed = 0;

        foreach (var d in registry.Snapshot())
        {
            seed++;
            var def = MetricCatalog.Get(d.Metric);
            double noise = SignalModel.Noise(d.Metric);

            // Readings arrive in hourly bursts → each burst becomes one row of a jagged array.
            var burst = new List<TelemetryEnvelope>(3600 / step + 1);
            DateTime? burstHour = null;

            for (var t = start; t < now.AddSeconds(-step); t = t.AddSeconds(step))
            {
                double v = SignalModel.Baseline(d.Metric, t, seed) + noise * SignalModel.Gaussian(rng);
                if (def.Kind != ValueKind.Bool && rng.NextDouble() < 0.002) v += noise * 9; // occasional historical spike
                var env = MakeEnvelope(d.Mac, def, v, t);

                history.Record(d.Mac, t, env.Numeric, includeTimeline: t > now.AddMinutes(-15));
                if (t > now.AddMinutes(-60)) anomalies.Observe(d.Mac, env.Numeric, t, def.Kind == ValueKind.Bool);

                var hour = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);
                if (burstHour != hour && burst.Count > 0)
                {
                    FlushBurst(def.Kind, burst);
                    burst.Clear();
                }
                burstHour = hour;
                burst.Add(env);
                total++;
            }
            if (burst.Count > 0)
            {
                var last = burst[^1];
                registry.Touch(d.Mac, last.Numeric, last.DisplayValue, def.Classify(last.Numeric), last.TimestampUtc, 0);
                if (def.Kind == ValueKind.Bool) registry.SetActuator(d.Mac, last.Numeric >= 0.5); // desired = last reported
                FlushBurst(def.Kind, burst);
            }
        }
        return total;
    }

    private void FlushBurst(ValueKind kind, List<TelemetryEnvelope> burst)
    {
        switch (kind)
        {
            case ValueKind.Float: hub.Floats.AddBurst(burst.Select(e => e.AsFloat()).ToArray()); break;
            case ValueKind.Int: hub.Ints.AddBurst(burst.Select(e => e.AsInt()).ToArray()); break;
            default: hub.Bools.AddBurst(burst.Select(e => e.AsBool()).ToArray()); break;
        }
    }

    private static TelemetryEnvelope MakeEnvelope(string mac, MetricDefinition def, double v, DateTime t) => def.Kind switch
    {
        ValueKind.Float => TelemetryEnvelope.From(new TelemetryPacket<float>(mac, def.Key, (float)Math.Round(v, 2), t, PacketQuality.Backfilled), 0),
        ValueKind.Int => TelemetryEnvelope.From(new TelemetryPacket<int>(mac, def.Key, (int)Math.Round(v), t, PacketQuality.Backfilled), 0),
        _ => TelemetryEnvelope.From(new TelemetryPacket<bool>(mac, def.Key, v >= 0.5, t, PacketQuality.Backfilled), 0)
    };

    /// <summary>Operator history the predictive engine learns from (as if recorded over the past week).</summary>
    private void SeedPatterns(DateTime now)
    {
        var byName = registry.Snapshot().ToDictionary(d => d.DisplayName, d => d.Mac);
        string M(string n) => byName[n];
        AlertService.AlertKey C(string n, string code) => new(M(n), code);

        var t = now.AddDays(-7);
        void Episode(AlertService.AlertKey cond, params Action<DateTime, IReadOnlyList<AlertService.AlertKey>>[] steps)
        {
            t = t.AddHours(7.3);
            var conditions = new[] { cond };
            for (int i = 0; i < steps.Length; i++) steps[i](t.AddMinutes(i * 1.5), conditions);
        }

        Action<DateTime, IReadOnlyList<AlertService.AlertKey>> Inspect(string n) => (at, c) => insights.RecordInspect(M(n), c, at);
        Action<DateTime, IReadOnlyList<AlertService.AlertKey>> Cmd(string n, CommandAction a) => (at, c) => insights.RecordCommand(M(n), a, c, at);

        // MOIST-B2 critically dry → operators inspect it, then start PUMP-A (7×) or open VALVE-B (2×).
        for (int i = 0; i < 7; i++) Episode(C("MOIST-B2", "CRITICAL_LOW"), Inspect("MOIST-B2"), Cmd("PUMP-A", CommandAction.On));
        for (int i = 0; i < 2; i++) Episode(C("MOIST-B2", "CRITICAL_LOW"), Inspect("MOIST-B2"), Cmd("VALVE-B", CommandAction.On));
        // Phase L2 overloaded → shed Block 1 feeder.
        for (int i = 0; i < 5; i++) Episode(C("METER-L2", "CRITICAL_HIGH"), Inspect("METER-L2"), Cmd("RELAY-BLK1", CommandAction.Off));
        // Bay A drying → open VALVE-A.
        for (int i = 0; i < 4; i++) Episode(C("MOIST-A1", "WARNING_LOW"), Cmd("VALVE-A", CommandAction.On));
        // Reservoir overheating → UV steriliser relay off.
        for (int i = 0; i < 3; i++) Episode(C("TEMP-RES", "CRITICAL_HIGH"), Inspect("TEMP-RES"), Cmd("RELAY-UV", CommandAction.Off));
        // Pure sequence: after checking pump power, operators look at the pump room voltage.
        for (int i = 0; i < 4; i++) Episode(C("VOLT-PR", "WARNING_LOW"), Inspect("POWER-PUMPS"), Inspect("VOLT-PR"));
    }

    private void SeedIssues(DateTime now)
    {
        var devices = registry.Snapshot();
        string[] titles =
        [
            "Moisture probe reading erratic after cleaning", "Valve chattering on open command", "Meter CT clamp loose",
            "Node rebooting every few minutes (brown-out?)", "Humidity stuck at 99 %", "Voltage dips during pump start"
        ];
        var rng = new Random(7);
        for (int i = 0; i < titles.Length; i++)
        {
            var d = devices[rng.Next(devices.Count)];
            engagement.SeedHistorical(d.Mac, d.DisplayName, titles[i], i % 3 == 0 ? Severity.Critical : Severity.Warning,
                now.AddHours(-30 + i * 4), 20 + rng.Next(70), 600 + rng.Next(3000));
        }
    }
}
