using SmartX.Core.Contracts;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Dynamic engagement strategy: <b>"Anomaly Radar" with one-click triage</b>.
/// <list type="bullet">
/// <item>A live device × time heatmap (built from a <c>double[,]</c> grid) where colour = how unusual the
/// reading is (|z-score|) and a grey gap = the sensor went silent. Spikes and dropouts become visible patterns
/// instead of numbers buried in a table.</item>
/// <item>Heartbeat pulses that show seconds since each node last reported.</item>
/// <item>A user-reported issue list (<c>List&lt;IssueReport&gt;</c>) with acknowledge/resolve, mean-time-to-acknowledge,
/// a triage streak and badges - light gamification that rewards fast responses rather than decoration.</item>
/// </list>
/// </summary>
public sealed class EngagementService(DeviceRegistry registry, AnomalyDetector anomalies, AlertService alerts)
{
    public const int Buckets = 18;
    public const int BucketSeconds = 10;

    private sealed class IssueReport
    {
        public int Id { get; init; }
        public required string Mac { get; init; }
        public required string DeviceName { get; init; }
        public required string Title { get; init; }
        public Severity Severity { get; init; }
        public required string Source { get; init; }
        public IssueStatus Status { get; set; }
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        public DateTime? AcknowledgedUtc { get; set; }
        public DateTime? ResolvedUtc { get; set; }
    }

    private readonly List<IssueReport> _issues = [];
    private readonly Lock _gate = new();
    private int _nextId;

    /// <summary>Auto-open an issue when a critical alert fires (de-duplicated per device).</summary>
    public void OnCriticalAlert(AlertDto alert)
    {
        if (alert.Code == "DISCONNECTED") return; // disconnects are shown on the radar; avoid flooding the list
        lock (_gate)
        {
            if (_issues.Exists(i => i.Mac == alert.Mac && i.Status != IssueStatus.Resolved)) return;
            _issues.Add(new IssueReport
            {
                Id = ++_nextId, Mac = alert.Mac, DeviceName = alert.DeviceName, Title = alert.Message,
                Severity = alert.Severity, Source = "Auto"
            });
        }
    }

    public (IssueDto? Issue, string? Error) Report(ReportIssueRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || req.Title.Trim().Length < 4) return (null, "Describe the issue (at least 4 characters).");
        if (!MacAddress.TryNormalize(req.Mac, out var mac) || !registry.TryGet(mac, out var device)) return (null, "Choose a registered device.");
        lock (_gate)
        {
            var issue = new IssueReport
            {
                Id = ++_nextId, Mac = mac, DeviceName = device.DisplayName, Title = req.Title.Trim(),
                Severity = req.Severity, Source = "Operator"
            };
            _issues.Add(issue);
            return (ToDto(issue), null);
        }
    }

    /// <summary>Seeds historical (already handled) issues so MTTA and badges have data on first launch.</summary>
    public void SeedHistorical(string mac, string name, string title, Severity severity, DateTime created, double ackAfterSec, double resolveAfterSec)
    {
        lock (_gate)
        {
            _issues.Add(new IssueReport
            {
                Id = ++_nextId, Mac = mac, DeviceName = name, Title = title, Severity = severity, Source = "Operator",
                CreatedUtc = created, Status = IssueStatus.Resolved,
                AcknowledgedUtc = created.AddSeconds(ackAfterSec), ResolvedUtc = created.AddSeconds(resolveAfterSec)
            });
        }
    }

    public IssueDto? Transition(int id, IssueStatus target)
    {
        lock (_gate)
        {
            var issue = _issues.Find(i => i.Id == id);
            if (issue is null) return null;
            var now = DateTime.UtcNow;
            if (target >= IssueStatus.Acknowledged && issue.AcknowledgedUtc is null) issue.AcknowledgedUtc = now;
            if (target == IssueStatus.Resolved) issue.ResolvedUtc = now;
            issue.Status = target;
            return ToDto(issue);
        }
    }

    public RadarDto Radar()
    {
        var now = DateTime.UtcNow;
        var devices = registry.Snapshot()
            .OrderBy(d => d.Zone, StringComparer.Ordinal)
            .ThenBy(d => d.DisplayName, StringComparer.Ordinal)
            .ToList();

        var macs = devices.Select(d => d.Mac).ToList();
        double[,] grid = anomalies.BuildHeatmap(macs, now, Buckets, BucketSeconds);

        // Convert the rectangular grid into a jagged array for JSON (System.Text.Json cannot serialise T[,]).
        int rows = grid.GetLength(0), cols = grid.GetLength(1);
        var cells = new double?[rows][];
        for (int r = 0; r < rows; r++)
        {
            cells[r] = new double?[cols];
            for (int c = 0; c < cols; c++)
                cells[r][c] = double.IsNaN(grid[r, c]) ? null : Math.Round(grid[r, c], 2);
        }

        var columnLabels = Enumerable.Range(0, Buckets)
            .Select(i => $"-{(Buckets - i) * BucketSeconds}s").ToList();

        var heartbeats = devices.Select(d => new HeartbeatDto(
            d.Mac, d.DisplayName, d.Zone,
            d.LastSeenUtc is null ? -1 : Math.Round((now - d.LastSeenUtc.Value).TotalSeconds, 1),
            d.Status, d.LastDisplay, d.AnomalyScore, d.Severity)).ToList();

        List<IssueDto> issues;
        double mtta;
        int resolved, streak;
        int openCount;
        lock (_gate)
        {
            issues = _issues.OrderBy(i => i.Status).ThenByDescending(i => i.Severity).ThenByDescending(i => i.CreatedUtc)
                .Take(25).Select(ToDto).ToList();
            var acked = _issues.Where(i => i.AcknowledgedUtc is not null).ToList();
            mtta = acked.Count == 0 ? 0 : Math.Round(acked.Average(i => (i.AcknowledgedUtc!.Value - i.CreatedUtc).TotalSeconds), 1);
            resolved = _issues.Count(i => i.Status == IssueStatus.Resolved);
            openCount = _issues.Count(i => i.Status == IssueStatus.Open);

            streak = 0; // consecutive most-recent issues acknowledged within 2 minutes
            foreach (var i in _issues.Where(i => i.Status != IssueStatus.Open).OrderByDescending(i => i.AcknowledgedUtc))
            {
                if ((i.AcknowledgedUtc!.Value - i.CreatedUtc).TotalSeconds <= 120) streak++;
                else break;
            }
        }

        int total = Math.Max(1, devices.Count);
        double offline = devices.Count(d => d.Status == DeviceStatus.Offline) / (double)total;
        double critical = devices.Count(d => d.Severity == Severity.Critical) / (double)total;
        double warning = devices.Count(d => d.Severity == Severity.Warning) / (double)total;
        int health = (int)Math.Round(Math.Clamp(100 - offline * 45 - critical * 30 - warning * 10 - Math.Min(15, openCount * 3), 0, 100));

        var badges = new List<string>();
        if (streak >= 3) badges.Add($"🔥 Triage streak ×{streak}");
        if (mtta is > 0 and <= 60) badges.Add("⚡ Rapid Responder (MTTA < 60 s)");
        if (openCount == 0) badges.Add("✅ Zero backlog");
        if (health >= 90) badges.Add("🛡️ Network Guardian");
        if (resolved >= 10) badges.Add("🎯 Spike Hunter (10+ resolved)");

        return new RadarDto(devices.Select(d => d.DisplayName).ToList(), columnLabels, cells, BucketSeconds,
            heartbeats, alerts.RecentSpikes(), issues, health, mtta, resolved, streak, badges);
    }

    private static IssueDto ToDto(IssueReport i) =>
        new(i.Id, i.Mac, i.DeviceName, i.Title, i.Severity, i.Status, i.Source, i.CreatedUtc, i.AcknowledgedUtc, i.ResolvedUtc);
}
