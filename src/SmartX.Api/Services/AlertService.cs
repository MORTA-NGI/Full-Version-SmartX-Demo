using SmartX.Core.Contracts;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Alert de-duplication with <b>HashSet&lt;T&gt;</b>.
/// <list type="bullet">
/// <item><c>HashSet&lt;string&gt; _disconnected</c> - the unique set of nodes currently offline. The watchdog
/// re-checks every 2 s; <c>Add</c> returns false for a node that is already known to be down, so the duplicate
/// alert is suppressed in O(1) instead of being processed (and shown) again.</item>
/// <item><c>HashSet&lt;AlertKey&gt; _activeErrors</c> - unique (device, error-code) pairs, e.g. MOIST-B2 + CRITICAL_LOW.
/// A sensor that keeps reporting 9 % moisture every second raises exactly one alert until it recovers.</item>
/// </list>
/// </summary>
public sealed class AlertService
{
    public readonly record struct AlertKey(string Mac, string Code);

    private static readonly string[] ThresholdCodes = ["CRITICAL_LOW", "CRITICAL_HIGH", "WARNING_LOW", "WARNING_HIGH"];

    private readonly HashSet<string> _disconnected = new(StringComparer.Ordinal);
    private readonly HashSet<AlertKey> _activeErrors = [];
    private readonly Dictionary<AlertKey, AlertDto> _activeDetails = [];
    private readonly LinkedList<AlertDto> _feed = new();      // newest first, capped
    private readonly LinkedList<SpikeDto> _spikes = new();    // feed for the Anomaly Radar
    private readonly Lock _gate = new();
    private long _nextId;
    private long _duplicatesSuppressed;

    public event Action<AlertDto>? CriticalRaised;

    public long DuplicatesSuppressed => Interlocked.Read(ref _duplicatesSuppressed);

    // ───────── disconnects ─────────
    public bool MarkDisconnected(string mac, string name)
    {
        AlertDto? raised = null;
        lock (_gate)
        {
            if (!_disconnected.Add(mac))
            {
                _duplicatesSuppressed++; // already known → duplicate suppressed in O(1)
                return false;
            }
            raised = RaiseLocked(new AlertKey(mac, "DISCONNECTED"), name, Severity.Critical,
                $"{name} stopped reporting - no packet within the offline window.", trackActive: true);
            AddSpikeLocked(new SpikeDto(mac, name, "heartbeat", "no signal", 0, DateTime.UtcNow, "Disconnect"));
        }
        CriticalRaised?.Invoke(raised);
        return true;
    }

    public void MarkSeen(string mac, string name)
    {
        lock (_gate)
        {
            if (!_disconnected.Remove(mac)) return; // fast path: node was not down
            ClearLocked(new AlertKey(mac, "DISCONNECTED"), name, $"{name} reconnected and is reporting again.");
        }
    }

    // ───────── thresholds ─────────
    public void EvaluateThreshold(string mac, string name, MetricDefinition def, double value, Severity severity, string display)
    {
        string? code = severity switch
        {
            Severity.Critical => value <= def.CriticalLow ? "CRITICAL_LOW" : "CRITICAL_HIGH",
            Severity.Warning => value < def.NormalMin ? "WARNING_LOW" : "WARNING_HIGH",
            _ => null
        };

        AlertDto? critical = null;
        lock (_gate)
        {
            foreach (var c in ThresholdCodes)
            {
                if (c != code) ClearLocked(new AlertKey(mac, c), name, $"{name} {def.Label.ToLowerInvariant()} back to {display} {def.Unit}.");
            }
            if (code is null) return;

            var key = new AlertKey(mac, code);
            if (_activeErrors.Contains(key))
            {
                _duplicatesSuppressed++;
                return;
            }
            var alert = RaiseLocked(key, name, severity,
                $"{def.Label} {display} {def.Unit} is {(code.EndsWith("LOW") ? "below" : "above")} the {(severity == Severity.Critical ? "critical" : "normal")} band.",
                trackActive: true);
            AddSpikeLocked(new SpikeDto(mac, name, def.Key, $"{display} {def.Unit}", 0, DateTime.UtcNow, severity == Severity.Critical ? "Critical" : "Threshold"));
            if (severity == Severity.Critical) critical = alert;
        }
        if (critical is not null) CriticalRaised?.Invoke(critical);
    }

    // ───────── statistical spikes ─────────
    public void EvaluateSpike(string mac, string name, string metric, double z, string display)
    {
        var key = new AlertKey(mac, "SPIKE");
        lock (_gate)
        {
            if (Math.Abs(z) >= AnomalyDetector.SpikeThreshold)
            {
                AddSpikeLocked(new SpikeDto(mac, name, metric, display, Math.Round(z, 1), DateTime.UtcNow, "Spike"));
                if (_activeErrors.Contains(key)) { _duplicatesSuppressed++; return; }
                RaiseLocked(key, name, Severity.Warning, $"Sudden {(z > 0 ? "jump" : "drop")} on {name}: {display} (z = {z:0.0}).", trackActive: true);
            }
            else if (Math.Abs(z) < 2)
            {
                ClearLocked(key, name, null);
            }
        }
    }

    // ───────── queries ─────────
    public IReadOnlyList<string> ActiveFor(string mac)
    {
        lock (_gate)
        {
            var list = new List<string>(2);
            foreach (var k in _activeErrors) if (k.Mac == mac) list.Add(k.Code);
            return list;
        }
    }

    /// <summary>Active (mac, code) pairs - consumed by the predictive engine as "conditions".</summary>
    public List<AlertKey> ActiveConditions()
    {
        lock (_gate) return [.. _activeErrors];
    }

    public bool IsDisconnected(string mac)
    {
        lock (_gate) return _disconnected.Contains(mac);
    }

    public (List<string> Disconnected, List<string> ActiveErrors, List<AlertDto> Feed, long Duplicates) Snapshot(int feedSize = 25)
    {
        lock (_gate)
        {
            return (
                _disconnected.Order().ToList(),
                _activeErrors.Select(k => $"{k.Mac} · {k.Code}").Order().ToList(),
                _feed.Take(feedSize).ToList(),
                _duplicatesSuppressed);
        }
    }

    public List<SpikeDto> RecentSpikes(int max = 15)
    {
        lock (_gate) return _spikes.Take(max).ToList();
    }

    // ───────── internals (call with _gate held) ─────────
    private AlertDto RaiseLocked(AlertKey key, string name, Severity severity, string message, bool trackActive)
    {
        var alert = new AlertDto(++_nextId, key.Mac, name, key.Code, severity, message, DateTime.UtcNow, true);
        if (trackActive)
        {
            _activeErrors.Add(key);
            _activeDetails[key] = alert;
        }
        _feed.AddFirst(alert);
        while (_feed.Count > 200) _feed.RemoveLast();
        return alert;
    }

    private void ClearLocked(AlertKey key, string name, string? recoveryMessage)
    {
        if (!_activeErrors.Remove(key)) return;
        _activeDetails.Remove(key);
        if (recoveryMessage is null) return;
        _feed.AddFirst(new AlertDto(++_nextId, key.Mac, name, $"{key.Code}_CLEARED", Severity.Normal, recoveryMessage, DateTime.UtcNow, false));
        while (_feed.Count > 200) _feed.RemoveLast();
    }

    private void AddSpikeLocked(SpikeDto spike)
    {
        _spikes.AddFirst(spike);
        while (_spikes.Count > 60) _spikes.RemoveLast();
    }
}
