using SmartX.Core.Contracts;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>Mutable server-side state of one registered sensor.</summary>
public sealed class SensorDevice
{
    public required string Mac { get; init; }
    public required string DisplayName { get; init; }
    public required SensorCategory Category { get; init; }
    public required MetricDefinition Metric { get; init; }
    public required string NodeId { get; init; }
    public required string LocationPath { get; init; }
    public required string Zone { get; init; }
    public required string FacilityId { get; init; }
    public DateTime RegisteredUtc { get; init; } = DateTime.UtcNow;

    public DateTime? LastSeenUtc { get; set; }
    public double? LastValue { get; set; }
    public string LastDisplay { get; set; } = "—";
    public Severity LastSeverity { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Offline;
    public double AnomalyScore { get; set; }

    /// <summary>Desired actuator state set by operator commands (null for non-actuators).</summary>
    public bool? ActuatorOn { get; set; }

    public int AttachmentCount { get; set; }
}

/// <summary>
/// Live device registry: <c>Dictionary&lt;string, SensorDevice&gt;</c> keyed by the normalised MAC address.
/// Every incoming packet is matched to its device with an O(1) hash lookup instead of scanning a list -
/// with 10 000 nodes publishing every second that is the difference between ~10k and ~100M comparisons per second.
/// MACs are normalised (upper-case, colon separated) before use, so the default ordinal comparer is both
/// correct and the fastest option.
/// </summary>
public sealed class DeviceRegistry
{
    private readonly Dictionary<string, SensorDevice> _byMac = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public int Count
    {
        get { lock (_gate) return _byMac.Count; }
    }

    public bool Contains(string mac)
    {
        lock (_gate) return _byMac.ContainsKey(mac);
    }

    public bool TryGet(string mac, out SensorDevice device)
    {
        lock (_gate) return _byMac.TryGetValue(mac, out device!);
    }

    public bool TryAdd(SensorDevice device)
    {
        lock (_gate)
        {
            if (!_byMac.TryAdd(device.Mac, device)) return false;
            if (device.Category == SensorCategory.Actuator) device.ActuatorOn ??= false;
            return true;
        }
    }

    public int CountOnNode(string nodeId)
    {
        lock (_gate) return _byMac.Values.Count(d => string.Equals(d.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
    }

    public Dictionary<string, int> CountsByNode()
    {
        lock (_gate)
        {
            return _byMac.Values.GroupBy(d => d.NodeId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Applies a processed reading to the device (called by the stream processor).</summary>
    /// <returns>True when this reading is the newest one seen for the device (i.e. it represents live state).</returns>
    public bool Touch(string mac, double value, string display, Severity severity, DateTime timestampUtc, double anomalyScore)
    {
        lock (_gate)
        {
            if (!_byMac.TryGetValue(mac, out var d)) return false;
            bool latest = d.LastSeenUtc is null || timestampUtc >= d.LastSeenUtc;
            if (latest) // ignore out-of-order backfill for "live" state
            {
                d.LastValue = Math.Round(value, 3);
                d.LastDisplay = display;
                d.LastSeverity = severity;
                d.LastSeenUtc = timestampUtc > DateTime.UtcNow ? DateTime.UtcNow : timestampUtc;
            }
            d.AnomalyScore = anomalyScore;
            d.Status = DeviceStatus.Online;
            return latest;
        }
    }

    /// <summary>Recomputes Online/Stale/Offline from last-seen timestamps. Returns devices that are offline.</summary>
    public List<(string Mac, string Name, bool JustWentOffline)> RefreshStatuses(DateTime nowUtc, TimeSpan stale, TimeSpan offline)
    {
        var offlineList = new List<(string, string, bool)>();
        lock (_gate)
        {
            foreach (var d in _byMac.Values)
            {
                var age = d.LastSeenUtc is null ? TimeSpan.MaxValue : nowUtc - d.LastSeenUtc.Value;
                var newStatus = age >= offline ? DeviceStatus.Offline : age >= stale ? DeviceStatus.Stale : DeviceStatus.Online;
                bool justOffline = newStatus == DeviceStatus.Offline && d.Status != DeviceStatus.Offline;
                d.Status = newStatus;
                if (newStatus == DeviceStatus.Offline) offlineList.Add((d.Mac, d.DisplayName, justOffline));
            }
        }
        return offlineList;
    }

    /// <summary>Sets the desired actuator state and returns the previous one.</summary>
    public bool? SetActuator(string mac, bool on)
    {
        lock (_gate)
        {
            if (!_byMac.TryGetValue(mac, out var d) || d.Category != SensorCategory.Actuator) return null;
            var before = d.ActuatorOn ?? false;
            d.ActuatorOn = on;
            return before;
        }
    }

    public Dictionary<string, bool> ActuatorStates()
    {
        lock (_gate)
        {
            return _byMac.Values.Where(d => d.Category == SensorCategory.Actuator)
                .ToDictionary(d => d.Mac, d => d.ActuatorOn ?? false);
        }
    }

    public void AdjustAttachmentCount(string mac, int delta)
    {
        lock (_gate)
        {
            if (_byMac.TryGetValue(mac, out var d)) d.AttachmentCount = Math.Max(0, d.AttachmentCount + delta);
        }
    }

    /// <summary>Thread-safe copy of every device as a DTO.</summary>
    public List<DeviceDto> Snapshot(Func<string, IReadOnlyList<string>>? alertsFor = null)
    {
        lock (_gate)
        {
            var list = new List<DeviceDto>(_byMac.Count);
            foreach (var d in _byMac.Values) list.Add(ToDto(d, alertsFor?.Invoke(d.Mac) ?? []));
            return list;
        }
    }

    public DeviceDto? Get(string mac, Func<string, IReadOnlyList<string>>? alertsFor = null)
    {
        lock (_gate)
        {
            return _byMac.TryGetValue(mac, out var d) ? ToDto(d, alertsFor?.Invoke(d.Mac) ?? []) : null;
        }
    }

    public static DeviceDto ToDto(SensorDevice d, IReadOnlyList<string> alerts) => new(
        d.Mac, d.DisplayName, d.Category, d.Metric.Key, d.Metric.Label, d.Metric.Unit, d.Metric.Kind,
        d.NodeId, d.LocationPath, d.Zone, d.FacilityId, d.Status, d.RegisteredUtc, d.LastSeenUtc, d.LastValue, d.LastDisplay,
        d.LastSeverity, d.ActuatorOn, d.AttachmentCount, Math.Round(d.AnomalyScore, 2), alerts);
}
