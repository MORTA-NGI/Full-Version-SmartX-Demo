using SmartX.Core.Contracts;
using SmartX.Core.Devices;

namespace SmartX.Api.Services;

/// <summary>
/// Predictive Action &amp; Recommendation Engine.
/// <para>Three learned models, all built from nested dictionaries (O(1) updates and lookups):</para>
/// <list type="number">
/// <item><b>Condition → action model</b> <c>Dictionary&lt;condition, Dictionary&lt;action, count&gt;&gt;</c>:
/// while "MOIST-B2 is CRITICAL_LOW" was active, what did operators do? If 7 of 9 responses were
/// "PUMP-A ON", the next time MOIST-B2 drops we suggest it with 78 % confidence <i>before</i> anyone searches.
/// Conditions are also generalised by metric (e.g. any soil_moisture CRITICAL_LOW) so new sensors benefit too.</item>
/// <item><b>Sequence model</b> (first-order Markov chain) <c>Dictionary&lt;lastAction, Dictionary&lt;nextAction, count&gt;&gt;</c>:
/// "operators who search for Moisture Sensor B usually open Pump A next".</item>
/// <item><b>Fault spotlight</b>: live devices ranked by severity / anomaly score / disconnect.</item>
/// </list>
/// Candidates from all three are merged (best score per action; severity adds a boost to the ranking score, while the
/// card still shows the raw learned probability) and the top N are extracted with a
/// <b>PriorityQueue</b> (heap) ordered by score.
/// </summary>
public sealed class InsightEngine(DeviceRegistry registry, AlertService alerts)
{
    private sealed record UserAction(string Key, string Kind, string? Mac, CommandAction? Action, DateTime At);

    private static readonly TimeSpan SequenceWindow = TimeSpan.FromMinutes(10);

    private readonly List<UserAction> _history = [];
    private readonly Dictionary<string, Dictionary<string, int>> _transitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _conditionActions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _conditionTotals = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private UserAction? _last;

    public int LearnedActions
    {
        get { lock (_gate) return _history.Count; }
    }

    // ───────────── recording ─────────────
    public void RecordSearch(string query, IReadOnlyList<DeviceDto> matches, IReadOnlyList<AlertService.AlertKey> conditions, DateTime? at = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        // A focused search (1-3 hits) is treated as "the operator wanted to inspect that device".
        var action = matches.Count is > 0 and <= 3
            ? new UserAction($"inspect:{matches[0].Mac}", "inspect", matches[0].Mac, null, at ?? DateTime.UtcNow)
            : new UserAction($"search:{query.Trim().ToLowerInvariant()}", "search", null, null, at ?? DateTime.UtcNow);
        Record(action, conditions);
    }

    public void RecordInspect(string mac, IReadOnlyList<AlertService.AlertKey> conditions, DateTime? at = null) =>
        Record(new UserAction($"inspect:{mac}", "inspect", mac, null, at ?? DateTime.UtcNow), conditions);

    public void RecordCommand(string mac, CommandAction action, IReadOnlyList<AlertService.AlertKey> conditions, DateTime? at = null) =>
        Record(new UserAction($"cmd:{mac}:{action}", "command", mac, action, at ?? DateTime.UtcNow), conditions);

    private void Record(UserAction action, IReadOnlyList<AlertService.AlertKey> conditions)
    {
        // Resolve the generalised (metric-level) condition keys before taking our lock.
        var conditionKeys = new List<string>(conditions.Count * 2);
        foreach (var c in conditions)
        {
            if (c.Mac == action.Mac) continue; // reacting to your own device's state is not a pattern
            conditionKeys.Add($"{c.Mac}|{c.Code}");
            if (registry.TryGet(c.Mac, out var dev)) conditionKeys.Add($"metric:{dev.Metric.Key}|{c.Code}");
        }

        lock (_gate)
        {
            if (_last is not null && action.At - _last.At <= SequenceWindow && action.At >= _last.At && _last.Key != action.Key)
                Increment(_transitions, _last.Key, action.Key);

            foreach (var ck in conditionKeys)
            {
                Increment(_conditionActions, ck, action.Key);
                _conditionTotals[ck] = _conditionTotals.GetValueOrDefault(ck) + 1;
            }

            _history.Add(action);
            if (_history.Count > 5000) _history.RemoveRange(0, 1000);
            _last = action;
        }
    }

    private static void Increment(Dictionary<string, Dictionary<string, int>> map, string from, string to)
    {
        if (!map.TryGetValue(from, out var inner)) map[from] = inner = new Dictionary<string, int>(StringComparer.Ordinal);
        inner[to] = inner.GetValueOrDefault(to) + 1;
    }

    // ───────────── suggesting ─────────────
    public List<SuggestionDto> Suggest(int max = 6)
    {
        var devices = registry.Snapshot(alerts.ActiveFor).ToDictionary(d => d.Mac, StringComparer.Ordinal);
        var conditions = alerts.ActiveConditions();
        var candidates = new Dictionary<string, (SuggestionDto Dto, double Score)>(StringComparer.Ordinal);

        void Offer(SuggestionDto s, double score)
        {
            if (!candidates.TryGetValue(s.Id, out var existing) || existing.Score < score) candidates[s.Id] = (s, score);
        }

        lock (_gate)
        {
            // 1) Learned condition → action associations.
            foreach (var cond in conditions)
            {
                if (!devices.TryGetValue(cond.Mac, out var condDevice)) continue;
                foreach (var ck in new[] { $"{cond.Mac}|{cond.Code}", $"metric:{condDevice.Metric}|{cond.Code}" })
                {
                    if (!_conditionActions.TryGetValue(ck, out var actions)) continue;
                    int total = _conditionTotals.GetValueOrDefault(ck);
                    bool generalised = ck.StartsWith("metric:", StringComparison.Ordinal);
                    foreach (var (actionKey, count) in actions)
                    {
                        if (count < 2 || total == 0) continue;
                        double confidence = (double)count / total * (generalised ? 0.85 : 1.0);
                        var s = Build(actionKey, devices, confidence, "Learned pattern",
                            $"{condDevice.DisplayName} is {Pretty(cond.Code)}. In {count} of {total} past responses to " +
                            $"{(generalised ? $"this condition on any {condDevice.MetricLabel.ToLowerInvariant()} sensor" : "this exact condition")}, operators did this.",
                            $"{count}/{total} responses");
                        if (s is not null) Offer(s, confidence + (cond.Code.StartsWith("CRITICAL") || cond.Code == "DISCONNECTED" ? 0.25 : 0.1));
                    }
                }
            }

            // 2) Markov sequence: what usually follows the operator's last action.
            if (_last is not null && DateTime.UtcNow - _last.At <= TimeSpan.FromMinutes(15) &&
                _transitions.TryGetValue(_last.Key, out var next))
            {
                int sum = next.Values.Sum();
                foreach (var (actionKey, count) in next.OrderByDescending(kv => kv.Value).Take(3))
                {
                    double p = (double)count / sum;
                    var s = Build(actionKey, devices, p, "Next step",
                        $"After \"{Describe(_last.Key, devices)}\" operators went on to do this {count} of {sum} times.",
                        $"P = {p:P0}");
                    if (s is not null) Offer(s, p * 0.8);
                }
            }
        }

        // 3) Fault spotlight + domain rules from the live state (offline nodes capped so they can't crowd out the rest).
        int offlineOffered = 0, erraticOffered = 0;
        foreach (var d in devices.Values.OrderByDescending(x => x.Severity).ThenByDescending(x => x.AnomalyScore))
        {
            double score = 0;
            string reason = "";
            if (d.Status == DeviceStatus.Offline)
            {
                if (++offlineOffered > 2) continue;
                score = 0.45; reason = $"{d.DisplayName} has stopped reporting - check power or radio link.";
            }
            else if (d.Severity == SmartX.Core.Telemetry.Severity.Critical) { score = 0.9; reason = $"{d.DisplayName} reads {d.LastDisplay} {d.Unit} - outside the critical band."; }
            else if (d.Severity == SmartX.Core.Telemetry.Severity.Warning) { score = 0.5; reason = $"{d.DisplayName} reads {d.LastDisplay} {d.Unit} - drifting out of range."; }
            else if (d.AnomalyScore >= 3 && ++erraticOffered <= 2) { score = Math.Min(0.6, d.AnomalyScore / 8); reason = $"{d.DisplayName} is behaving erratically (anomaly score {d.AnomalyScore:0.0})."; }
            if (score > 0)
            {
                Offer(new SuggestionDto($"inspect:{d.Mac}", $"Inspect {d.DisplayName}", reason, Math.Round(score, 2), "Inspect", d.Mac, null,
                    "Live fault spotlight"), score);
            }
        }

        foreach (var cond in conditions)
        {
            if (!devices.TryGetValue(cond.Mac, out var src)) continue;
            if (src.Metric == "soil_moisture" && cond.Code.EndsWith("LOW"))
            {
                foreach (var act in devices.Values.Where(a => a.Zone == src.Zone && a.Metric is "pump_state" or "valve_state" && a.ActuatorOn == false))
                {
                    Offer(new SuggestionDto($"cmd:{act.Mac}:On", $"Switch ON {act.DisplayName}",
                        $"Soil moisture in {src.Zone} is {src.LastDisplay} % and {act.DisplayName} is off.", 0.55, "Command", act.Mac, CommandAction.On,
                        "Irrigation rule"), 0.55);
                }
            }
            if (src.Metric == "power_w" && cond.Code == "CRITICAL_HIGH")
            {
                foreach (var act in devices.Values.Where(a => a.Zone == src.Zone && a.Metric == "relay_state" && a.ActuatorOn == true))
                {
                    Offer(new SuggestionDto($"cmd:{act.Mac}:Off", $"Shed load: switch OFF {act.DisplayName}",
                        $"{src.DisplayName} is drawing {src.LastDisplay} W, above the breaker limit.", 0.6, "Command", act.Mac, CommandAction.Off,
                        "Load-shedding rule"), 0.6);
                }
            }
        }

        // Top-N extraction with a heap: priority = −score so the best suggestion dequeues first.
        var heap = new PriorityQueue<SuggestionDto, double>();
        foreach (var (dto, score) in candidates.Values) heap.Enqueue(dto, -score); // rank by score, display the raw probability
        var result = new List<SuggestionDto>(max);
        while (result.Count < max && heap.TryDequeue(out var s, out _)) result.Add(s);
        return result;
    }

    public List<InsightPatternDto> Patterns(int max = 10)
    {
        var names = registry.Snapshot().ToDictionary(d => d.Mac, StringComparer.Ordinal);
        lock (_gate)
        {
            var list = new List<InsightPatternDto>();
            foreach (var (from, nexts) in _conditionActions.Where(k => !k.Key.StartsWith("metric:")))
            {
                int total = _conditionTotals.GetValueOrDefault(from);
                var parts = from.Split('|');
                string cond = names.TryGetValue(parts[0], out var d) ? $"{d.DisplayName} {Pretty(parts[1])}" : from;
                foreach (var (to, count) in nexts)
                    if (count >= 2) list.Add(new InsightPatternDto("Condition", cond, Describe(to, names), count, Math.Round((double)count / total, 2)));
            }
            foreach (var (from, nexts) in _transitions)
            {
                int sum = nexts.Values.Sum();
                foreach (var (to, count) in nexts)
                    if (count >= 2) list.Add(new InsightPatternDto("Sequence", Describe(from, names), Describe(to, names), count, Math.Round((double)count / sum, 2)));
            }
            return list.OrderByDescending(p => p.Count).ThenByDescending(p => p.Probability).Take(max).ToList();
        }
    }

    private static SuggestionDto? Build(string actionKey, IReadOnlyDictionary<string, DeviceDto> devices, double confidence, string evidence, string reason, string stat)
    {
        var parts = actionKey.Split(':', 2);
        switch (parts[0])
        {
            case "cmd":
            {
                int idx = parts[1].LastIndexOf(':');
                var mac = parts[1][..idx];
                if (!Enum.TryParse<CommandAction>(parts[1][(idx + 1)..], out var action)) return null;
                if (!devices.TryGetValue(mac, out var d)) return null;
                if (action == CommandAction.On && d.ActuatorOn == true) return null;   // already in that state
                if (action == CommandAction.Off && d.ActuatorOn == false) return null;
                return new SuggestionDto(actionKey, $"Switch {(action == CommandAction.On ? "ON" : "OFF")} {d.DisplayName}", reason,
                    Math.Round(confidence, 2), "Command", mac, action, $"{evidence} · {stat}");
            }
            case "inspect":
            {
                if (!devices.TryGetValue(parts[1], out var d)) return null;
                return new SuggestionDto(actionKey, $"Inspect {d.DisplayName}", reason, Math.Round(confidence, 2), "Inspect", d.Mac, null, $"{evidence} · {stat}");
            }
            default:
                return null;
        }
    }

    private static string Describe(string key, IReadOnlyDictionary<string, DeviceDto> devices)
    {
        var parts = key.Split(':', 2);
        if (parts[0] == "search") return $"search \"{parts[1]}\"";
        if (parts[0] == "inspect") return devices.TryGetValue(parts[1], out var d) ? $"inspect {d.DisplayName}" : key;
        if (parts[0] == "cmd")
        {
            int idx = parts[1].LastIndexOf(':');
            var mac = parts[1][..idx];
            var name = devices.TryGetValue(mac, out var d) ? d.DisplayName : mac;
            return $"{name} {parts[1][(idx + 1)..].ToUpperInvariant()}";
        }
        return key;
    }

    private static string Pretty(string code) => code.Replace('_', ' ').ToLowerInvariant();
}
