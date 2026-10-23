using System.Diagnostics;
using Microsoft.Extensions.Options;
using SmartX.Core.Collections;
using SmartX.Core.Contracts;

namespace SmartX.Api.Services;

/// <summary>
/// Two complementary history indexes per device:
/// <list type="bullet">
/// <item><b>SortedDictionary&lt;DateTime, double&gt;</b> (Part 2) - a short rolling timeline (~15 min) that is
/// always kept in timestamp order, so the dashboard can draw timeline graphs by simply enumerating it.</item>
/// <item><b>AvlTree&lt;long, double&gt;</b> (Part 3) - the long-term archive (days of data) keyed by ticks, which
/// answers "readings between 14:00 and 16:00" in O(log n + k).</item>
/// </list>
/// </summary>
public sealed class HistoryService(IOptions<GatewayOptions> options)
{
    private const int TimelineCapacity = 900;

    private readonly Dictionary<string, AvlTree<long, double>> _archive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<DateTime, double>> _timelines = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly int _maxPerDevice = options.Value.MaxHistoryPerDevice;

    public void Record(string mac, DateTime atUtc, double value, bool includeTimeline = true)
    {
        lock (_gate)
        {
            if (!_archive.TryGetValue(mac, out var tree)) _archive[mac] = tree = new AvlTree<long, double>();
            tree.Insert(atUtc.Ticks, value);
            while (tree.Count > _maxPerDevice) tree.RemoveMin(); // retention

            if (!includeTimeline) return;
            if (!_timelines.TryGetValue(mac, out var tl)) _timelines[mac] = tl = new SortedDictionary<DateTime, double>();

            var key = atUtc;
            while (tl.ContainsKey(key)) key = key.AddTicks(1); // keep both readings if two land on the same tick
            tl[key] = value;                                    // O(log n) ordered insert
            while (tl.Count > TimelineCapacity) tl.Remove(tl.Keys.First()); // drop oldest
        }
    }

    /// <summary>Ordered timeline points since <paramref name="sinceUtc"/> - already sorted, no OrderBy needed.</summary>
    public List<TimelinePointDto> Timeline(string mac, DateTime sinceUtc, int maxPoints = 180)
    {
        lock (_gate)
        {
            if (!_timelines.TryGetValue(mac, out var tl)) return [];
            var points = new List<TimelinePointDto>();
            foreach (var (t, v) in tl)
            {
                if (t >= sinceUtc) points.Add(new TimelinePointDto(t, v));
            }
            return Downsample(points, maxPoints);
        }
    }

    public HistoryQueryResultDto Query(string mac, string name, string unit, DateTime fromUtc, DateTime toUtc)
    {
        lock (_gate)
        {
            if (!_archive.TryGetValue(mac, out var tree))
                return new HistoryQueryResultDto(mac, name, unit, fromUtc, toUtc, 0, 0, 0, 0, 0, 0, null, null, null, [], []);

            var sw = Stopwatch.StartNew();
            var hits = tree.Range(fromUtc.Ticks, toUtc.Ticks, out int visited);
            sw.Stop();

            double? min = null, max = null, avg = null;
            if (hits.Count > 0)
            {
                double mn = double.MaxValue, mx = double.MinValue, sum = 0;
                foreach (var kv in hits)
                {
                    mn = Math.Min(mn, kv.Value);
                    mx = Math.Max(mx, kv.Value);
                    sum += kv.Value;
                }
                (min, max, avg) = (Math.Round(mn, 2), Math.Round(mx, 2), Math.Round(sum / hits.Count, 2));
            }

            var all = hits.Select(kv => new TimelinePointDto(new DateTime(kv.Key, DateTimeKind.Utc), Math.Round(kv.Value, 2))).ToList();
            var rows = all.Count <= 500 ? all : all.TakeLast(500).ToList();

            return new HistoryQueryResultDto(mac, name, unit, fromUtc, toUtc, hits.Count, visited, tree.Count, tree.Height,
                tree.Rotations, Math.Round(sw.Elapsed.TotalMicroseconds, 1), min, max, avg, Downsample(all, 400), rows);
        }
    }

    public double? AverageSince(string mac, DateTime sinceUtc)
    {
        lock (_gate)
        {
            if (!_archive.TryGetValue(mac, out var tree)) return null;
            var hits = tree.Range(sinceUtc.Ticks, DateTime.UtcNow.Ticks, out _);
            return hits.Count == 0 ? null : hits.Average(h => h.Value);
        }
    }

    public (DateTime? Oldest, DateTime? Newest, int Count) Coverage(string mac)
    {
        lock (_gate)
        {
            if (!_archive.TryGetValue(mac, out var tree) || tree.Count == 0) return (null, null, 0);
            tree.TryGetMin(out var lo, out _);
            tree.TryGetMax(out var hi, out _);
            return (new DateTime(lo, DateTimeKind.Utc), new DateTime(hi, DateTimeKind.Utc), tree.Count);
        }
    }

    /// <summary>Bucket-average downsampling so charts stay light no matter how many points the tree returns.</summary>
    private static List<TimelinePointDto> Downsample(List<TimelinePointDto> points, int max)
    {
        if (points.Count <= max) return points;
        var result = new List<TimelinePointDto>(max);
        double step = (double)points.Count / max;
        for (int b = 0; b < max; b++)
        {
            int start = (int)(b * step), end = Math.Min(points.Count, (int)((b + 1) * step));
            if (end <= start) continue;
            double sum = 0;
            for (int i = start; i < end; i++) sum += points[i].V;
            result.Add(new TimelinePointDto(points[(start + end) / 2].T, Math.Round(sum / (end - start), 2)));
        }
        return result;
    }
}
