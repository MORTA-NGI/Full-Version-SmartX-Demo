namespace SmartX.Api.Services;

/// <summary>
/// Rolling z-score spike detector (the engine behind the "Anomaly Radar" engagement strategy).
/// Each device keeps a fixed 60-sample ring buffer; the mean / standard deviation of that window tells us how
/// unusual a new reading is: z = (x − μ) / σ. |z| ≥ 4 is flagged as a spike. A second ring keeps the recent
/// |z| values with timestamps so the dashboard can paint a time × device heatmap.
/// </summary>
public sealed class AnomalyDetector
{
    private const int Window = 60;
    private const int TrailSize = 240;
    public const double SpikeThreshold = 4.0;

    private sealed class DeviceWindow
    {
        public readonly double[] Values = new double[Window];
        public int Count;
        public int Next;
        public double Ewma;
        public readonly (DateTime At, double AbsZ)[] Trail = new (DateTime, double)[TrailSize];
        public int TrailNext;
        public int TrailCount;
    }

    private readonly Dictionary<string, DeviceWindow> _windows = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Feeds a reading and returns (z-score, smoothed anomaly score).</summary>
    public (double Z, double Score) Observe(string mac, double value, DateTime atUtc, bool isBinary)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(mac, out var w)) _windows[mac] = w = new DeviceWindow();

            double z = 0;
            if (!isBinary && w.Count >= 10)
            {
                double mean = 0;
                for (int i = 0; i < w.Count; i++) mean += w.Values[i];
                mean /= w.Count;
                double variance = 0;
                for (int i = 0; i < w.Count; i++) variance += (w.Values[i] - mean) * (w.Values[i] - mean);
                double std = Math.Sqrt(variance / w.Count);
                std = Math.Max(std, Math.Max(Math.Abs(mean) * 0.01, 0.05)); // floor avoids divide-by-~0 on flat signals
                z = (value - mean) / std;
            }

            w.Values[w.Next] = value;
            w.Next = (w.Next + 1) % Window;
            w.Count = Math.Min(w.Count + 1, Window);
            w.Ewma = 0.8 * w.Ewma + 0.2 * Math.Abs(z);

            w.Trail[w.TrailNext] = (atUtc, Math.Abs(z));
            w.TrailNext = (w.TrailNext + 1) % TrailSize;
            w.TrailCount = Math.Min(w.TrailCount + 1, TrailSize);

            return (z, w.Ewma);
        }
    }

    /// <summary>
    /// Builds a <b>multi-dimensional</b> grid <c>double[rows, columns]</c>: one row per device, one column per
    /// time bucket. Each cell holds the max |z| seen in that bucket, or NaN when the device sent nothing
    /// (a gap = a dropout, which the UI paints grey).
    /// </summary>
    public double[,] BuildHeatmap(IReadOnlyList<string> macs, DateTime nowUtc, int buckets, int bucketSeconds)
    {
        var grid = new double[macs.Count, buckets];
        for (int r = 0; r < macs.Count; r++)
            for (int c = 0; c < buckets; c++)
                grid[r, c] = double.NaN;

        var start = nowUtc.AddSeconds(-buckets * bucketSeconds);
        lock (_gate)
        {
            for (int r = 0; r < macs.Count; r++)
            {
                if (!_windows.TryGetValue(macs[r], out var w)) continue;
                for (int i = 0; i < w.TrailCount; i++)
                {
                    var (at, absZ) = w.Trail[i];
                    if (at < start || at > nowUtc) continue;
                    int c = Math.Min(buckets - 1, (int)((at - start).TotalSeconds / bucketSeconds));
                    grid[r, c] = double.IsNaN(grid[r, c]) ? absZ : Math.Max(grid[r, c], absZ);
                }
            }
        }
        return grid;
    }
}
