using System.Net.Http.Json;
using SmartX.Core.Contracts;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;

// ─────────────────────────────────────────────────────────────────────────────
// Smart-X ESP32 fleet simulator
// Every registered sensor POSTs its reading to the gateway API once per tick -
// exactly like a real device would - so all data goes through the same HTTP
// ingestion path, validation, queues and alerting as real hardware.
//
//   dotnet run --project src/SmartX.DeviceSimulator -- --api http://localhost:5080
//   options: --interval 1000   (ms between ticks)
//            --no-dropouts     (disable random node brown-outs)
// ─────────────────────────────────────────────────────────────────────────────

string api = Environment.GetEnvironmentVariable("SMARTX_API") ?? "http://localhost:5080";
int intervalMs = 1000;
bool dropouts = true;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--api" when i + 1 < args.Length: api = args[++i]; break;
        case "--interval" when i + 1 < args.Length: intervalMs = Math.Clamp(int.Parse(args[++i]), 200, 60_000); break;
        case "--no-dropouts": dropouts = false; break;
    }
}

using var http = new HttpClient { BaseAddress = new Uri(api.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
var json = SmartXJson.Options;
var rng = new Random();
var state = new Dictionary<string, double>(StringComparer.Ordinal);       // last simulated value per MAC
var silentUntil = new Dictionary<string, DateTime>(StringComparer.Ordinal); // nodes currently "browned out"
var missed = new Dictionary<string, List<float>>(StringComparer.Ordinal);   // readings buffered while offline
List<DeviceDto> devices = [];
DateTime lastRefresh = DateTime.MinValue;
DateTime nextDropout = DateTime.UtcNow.AddSeconds(30);
DateTime nextSurge = DateTime.UtcNow.AddSeconds(90);
DateTime surgeUntil = DateTime.MinValue;
long tick = 0;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"Smart-X device simulator → {api}  (interval {intervalMs} ms, dropouts {(dropouts ? "on" : "off")})");
Console.WriteLine("Press Ctrl+C to stop.\n");

using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
try
{
    do
    {
        tick++;
        var now = DateTime.UtcNow;
        try
        {
            if (devices.Count == 0 || now - lastRefresh > TimeSpan.FromSeconds(30))
            {
                var result = await http.GetFromJsonAsync<DeviceSearchResultDto>("api/devices", json, cts.Token);
                devices = result?.Devices.ToList() ?? [];
                lastRefresh = now;
                foreach (var d in devices)
                    if (!state.ContainsKey(d.Mac))
                        state[d.Mac] = d.DisplayName == "MOIST-B2" ? 28 // the leaky bay starts dry so the scenario plays out within ~2 minutes
                            : d.LastValue ?? SignalModel.Baseline(d.Metric, now, Seed(d.Mac));
                Console.WriteLine($"[{now:HH:mm:ss}] fleet: {devices.Count} registered devices");
            }

            var actuators = await http.GetFromJsonAsync<Dictionary<string, bool>>("api/commands/actuators", json, cts.Token) ?? [];
            bool IsOn(string name) => devices.FirstOrDefault(d => d.DisplayName == name) is { } d && actuators.GetValueOrDefault(d.Mac);

            // ── scenario: random brown-outs (node goes silent, then backfills its buffered readings) ──
            if (dropouts && now >= nextDropout)
            {
                var candidates = devices.Where(d => d.DisplayName is not ("MOIST-B2" or "PUMP-A") && !silentUntil.ContainsKey(d.Mac)).ToList();
                if (candidates.Count > 0 && silentUntil.Count < 2)
                {
                    var victim = candidates[rng.Next(candidates.Count)];
                    silentUntil[victim.Mac] = now.AddSeconds(rng.Next(20, 40));
                    Console.WriteLine($"[{now:HH:mm:ss}] ⚡ brown-out: {victim.DisplayName} goes silent");
                }
                nextDropout = now.AddSeconds(rng.Next(35, 60));
            }

            // ── scenario: phase L2 surge every few minutes unless Block 1 load has been shed ──
            if (now >= nextSurge)
            {
                surgeUntil = now.AddSeconds(8);
                nextSurge = now.AddSeconds(rng.Next(150, 240));
                Console.WriteLine($"[{now:HH:mm:ss}] 🔺 load surge on METER-L2");
            }

            var sends = new List<Task<bool>>();
            using var gate = new SemaphoreSlim(16);
            int priorityHits = 0;

            foreach (var d in devices)
            {
                if (silentUntil.TryGetValue(d.Mac, out var until))
                {
                    double buffered = Next(d, now, IsOn);
                    if (d.Kind == ValueKind.Float) (missed.TryGetValue(d.Mac, out var l) ? l : missed[d.Mac] = []).Add((float)buffered);
                    if (now < until) continue;
                    silentUntil.Remove(d.Mac);
                    Console.WriteLine($"[{now:HH:mm:ss}] ✓ {d.DisplayName} back online");
                    if (missed.Remove(d.Mac, out var backlog) && backlog.Count > 1)
                        sends.Add(SendBackfill(d, backlog, now, intervalMs));
                    continue;
                }

                double v = Next(d, now, IsOn);
                if (d.DisplayName == "METER-L2" && now < surgeUntil) v = IsOn("RELAY-BLK1") ? 3900 + rng.Next(300) : 2100 + rng.Next(200);
                sends.Add(Send(d, v, gate, () => Interlocked.Increment(ref priorityHits)));
            }

            var results = await Task.WhenAll(sends);
            int ok = results.Count(r => r);
            if (tick % 5 == 0 || priorityHits > 0)
                Console.WriteLine($"[{now:HH:mm:ss}] tick {tick}: sent {ok}/{results.Length}" +
                                  (priorityHits > 0 ? $"  ⚠ {priorityHits} routed to PRIORITY lane" : "") +
                                  (silentUntil.Count > 0 ? $"  silent: {silentUntil.Count}" : ""));
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[{now:HH:mm:ss}] API unreachable ({ex.Message}) - retrying…");
            devices = [];
        }
        catch (TaskCanceledException) when (!cts.IsCancellationRequested)
        {
            Console.WriteLine($"[{now:HH:mm:ss}] request timed out - retrying…");
        }
    }
    while (await timer.WaitForNextTickAsync(cts.Token));
}
catch (OperationCanceledException)
{
    Console.WriteLine("Simulator stopped.");
}

// ───────────────────────── device physics ─────────────────────────
double Next(DeviceDto d, DateTime now, Func<string, bool> isOn)
{
    int seed = Seed(d.Mac);
    double baseline = SignalModel.Baseline(d.Metric, now, seed);
    double noise = SignalModel.Noise(d.Metric) * SignalModel.Gaussian(rng);
    double prev = state.GetValueOrDefault(d.Mac, baseline);
    double dt = intervalMs / 1000.0;
    double v;

    switch (d.Metric)
    {
        case "soil_moisture" when d.DisplayName == "MOIST-B2":
            // A leaky bay: dries out steadily until an operator runs PUMP-A or opens VALVE-B.
            v = isOn("PUMP-A") || isOn("VALVE-B") ? Math.Min(72, prev + 0.45 * dt) : Math.Max(4, prev - 0.09 * dt);
            v += noise * 0.3;
            break;
        case "soil_moisture":
            string valve = d.Zone.Contains("Sub-Zone A") ? "VALVE-A" : d.Zone.Contains("Sub-Zone C") ? "VALVE-C" : "VALVE-B";
            v = isOn(valve) ? Math.Min(85, prev + 0.2 * dt) : prev + (baseline - prev) * 0.05 + noise * 0.3;
            break;
        case "power_w" when d.DisplayName == "POWER-PUMPS":
            v = 140 + (isOn("PUMP-A") ? 950 : 0) + (isOn("PUMP-B") ? 900 : 0) + noise;
            break;
        case "pump_state" or "valve_state" or "relay_state":
            return isOn(d.DisplayName) ? 1 : 0;
        default:
            v = prev + (baseline - prev) * 0.15 + noise;
            break;
    }

    if (rng.NextDouble() < 0.003) v += SignalModel.Noise(d.Metric) * (rng.NextDouble() < 0.5 ? -9 : 9); // transient spike
    if (d.Metric is "power_w" or "soil_moisture" or "humidity") v = Math.Max(0, v);                // physical lower bound
    state[d.Mac] = v;
    return v;
}

int Seed(string mac) => Math.Abs(mac.GetHashCode() % 97);

async Task<bool> Send(DeviceDto d, double v, SemaphoreSlim gate, Action onPriority)
{
    await gate.WaitAsync(cts.Token);
    try
    {
        HttpResponseMessage resp = d.Kind switch
        {
            ValueKind.Float => await http.PostAsJsonAsync("api/telemetry/float", new TelemetryRequest<float>(d.Mac, d.Metric, (float)Math.Round(v, 2)), json, cts.Token),
            ValueKind.Int => await http.PostAsJsonAsync("api/telemetry/int", new TelemetryRequest<int>(d.Mac, d.Metric, (int)Math.Round(v)), json, cts.Token),
            _ => await http.PostAsJsonAsync("api/telemetry/bool", new TelemetryRequest<bool>(d.Mac, d.Metric, v >= 0.5), json, cts.Token),
        };
        if (!resp.IsSuccessStatusCode) return false;
        var ack = await resp.Content.ReadFromJsonAsync<IngestAck>(json, cts.Token);
        if (ack?.Lane == "PRIORITY") onPriority();
        return true;
    }
    finally
    {
        gate.Release();
    }
}

// The device buffered readings while offline; it uploads them as a jagged array of bursts.
async Task<bool> SendBackfill(DeviceDto d, List<float> backlog, DateTime now, int interval)
{
    var rows = new List<float[]>();
    for (int i = 0; i < backlog.Count;)
    {
        int len = Math.Min(backlog.Count - i, rng.Next(3, 9)); // bursts of uneven length → jagged
        rows.Add(backlog.GetRange(i, len).Select(x => (float)Math.Round(x, 2)).ToArray());
        i += len;
    }
    var start = now.AddMilliseconds(-backlog.Count * (double)interval);
    var resp = await http.PostAsJsonAsync("api/telemetry/batch",
        new BatchTelemetryRequest(d.Mac, d.Metric, rows.ToArray(), start, interval), json, cts.Token);
    if (resp.IsSuccessStatusCode)
    {
        var ack = await resp.Content.ReadFromJsonAsync<BatchAck>(json, cts.Token);
        Console.WriteLine($"[{now:HH:mm:ss}] ↺ {d.DisplayName} backfilled: {ack?.Message}");
    }
    return resp.IsSuccessStatusCode;
}
