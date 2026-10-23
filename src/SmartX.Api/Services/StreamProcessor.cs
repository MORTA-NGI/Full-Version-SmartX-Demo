using Microsoft.Extensions.Options;
using SmartX.Core.Contracts;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// The real-time ingestion engine.
/// <list type="bullet">
/// <item><b>Queue&lt;TelemetryEnvelope&gt;</b> - standard FIFO lane: packets are processed strictly in arrival order.</item>
/// <item><b>PriorityQueue&lt;TelemetryEnvelope, (int, long)&gt;</b> - critical lane: a severe power spike or a
/// moisture crash is classified at the API edge and placed here. Before every single dequeue the processor checks
/// this queue first, so a critical packet <i>bypasses</i> however many thousand normal packets are waiting.
/// Priority = (−severity, sequence): higher severity first, then FIFO among equals.</item>
/// </list>
/// The gateway's CPU budget is simulated with <see cref="GatewayOptions.MaxPacketsPerTick"/>, so a flood of traffic
/// builds a visible backlog and you can watch critical packets jump the queue.
/// </summary>
public sealed class StreamProcessor(
    IOptions<GatewayOptions> options,
    DeviceRegistry registry,
    TelemetryHub hub,
    AnomalyDetector anomalies,
    HistoryService history,
    AlertService alerts,
    ILogger<StreamProcessor> logger) : BackgroundService
{
    private readonly Queue<TelemetryEnvelope> _fifo = new();
    private readonly PriorityQueue<TelemetryEnvelope, (int NegSeverity, long Sequence)> _priority = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly LinkedList<ProcessedItemDto> _recent = new();
    private readonly GatewayOptions _opts = options.Value;

    private long _sequence;
    private int _signalPending;
    private long _processed, _priorityProcessed;
    private double _fifoLatencyEwma, _priorityLatencyEwma;
    private readonly long[] _rateStamps = new long[10]; // ring of per-second throughput buckets
    private readonly int[] _rateCounts = new int[10];

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>Routes a packet into the correct lane. Called by the HTTP endpoints (non-blocking).</summary>
    public string Enqueue(in TelemetryEnvelope envelope, Severity severity)
    {
        string lane;
        lock (_gate)
        {
            if (severity == Severity.Critical)
            {
                _priority.Enqueue(envelope, (-(int)severity, envelope.Sequence));
                lane = "PRIORITY";
            }
            else
            {
                _fifo.Enqueue(envelope);
                lane = "FIFO";
            }
        }
        if (Interlocked.Exchange(ref _signalPending, 1) == 0) _signal.Release();
        return lane;
    }

    public (int Fifo, int Priority) Depths()
    {
        lock (_gate) return (_fifo.Count, _priority.Count);
    }

    public long Processed => Interlocked.Read(ref _processed);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Stream processor started ({Budget} packets / {Tick} ms).", _opts.MaxPacketsPerTick, _opts.TickMilliseconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(TimeSpan.FromMilliseconds(500), stoppingToken);
                Volatile.Write(ref _signalPending, 0);

                bool more = true;
                while (more && !stoppingToken.IsCancellationRequested)
                {
                    int budget = _opts.MaxPacketsPerTick;
                    while (budget-- > 0 && TryDequeue(out var env, out var lane))
                    {
                        Process(env, lane);
                    }
                    var (f, p) = Depths();
                    more = f + p > 0;
                    if (more) await Task.Delay(_opts.TickMilliseconds, stoppingToken); // simulated CPU budget
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Stream processor iteration failed - continuing.");
            }
        }
    }

    /// <summary>Critical lane is ALWAYS checked first - this is the bypass.</summary>
    private bool TryDequeue(out TelemetryEnvelope env, out string lane)
    {
        lock (_gate)
        {
            if (_priority.TryDequeue(out env, out _)) { lane = "PRIORITY"; return true; }
            if (_fifo.TryDequeue(out env)) { lane = "FIFO"; return true; }
            lane = "";
            return false;
        }
    }

    private void Process(in TelemetryEnvelope env, string lane)
    {
        if (!registry.TryGet(env.Mac, out var device)) return;

        hub.Store(env);                                                       // typed, unboxed storage
        double value = env.Numeric;
        var (z, score) = anomalies.Observe(env.Mac, value, env.TimestampUtc, env.Kind == ValueKind.Bool);
        var severity = device.Metric.Classify(value);
        string display = env.DisplayValue;

        bool isLatest = registry.Touch(env.Mac, value, display, severity, env.TimestampUtc, score);
        history.Record(env.Mac, env.TimestampUtc, value);
        alerts.MarkSeen(env.Mac, device.DisplayName);
        if (isLatest) // an older (backfilled / delayed) packet must not clear or raise alerts for the live state
        {
            alerts.EvaluateThreshold(env.Mac, device.DisplayName, device.Metric, value, severity, display);
            if (env.Kind != ValueKind.Bool) alerts.EvaluateSpike(env.Mac, device.DisplayName, device.Metric.Key, z, $"{display} {device.Metric.Unit}");
        }

        var now = DateTime.UtcNow;
        double latency = (now - env.EnqueuedUtc).TotalMilliseconds;
        lock (_gate)
        {
            _processed++;
            if (lane == "PRIORITY")
            {
                _priorityProcessed++;
                _priorityLatencyEwma = _priorityLatencyEwma == 0 ? latency : 0.7 * _priorityLatencyEwma + 0.3 * latency;
            }
            else
            {
                _fifoLatencyEwma = _fifoLatencyEwma == 0 ? latency : 0.95 * _fifoLatencyEwma + 0.05 * latency;
            }

            _recent.AddFirst(new ProcessedItemDto(env.Sequence, env.Mac, device.DisplayName, env.Metric,
                $"{display} {device.Metric.Unit}", lane, Math.Round(latency, 1), now, severity));
            while (_recent.Count > 30) _recent.RemoveLast();

            long epochSecond = now.Ticks / TimeSpan.TicksPerSecond;
            int slot = (int)(epochSecond % _rateCounts.Length);
            if (_rateStamps[slot] != epochSecond) { _rateStamps[slot] = epochSecond; _rateCounts[slot] = 0; }
            _rateCounts[slot]++;
        }
    }

    public (long Processed, long PriorityProcessed, double PerSecond, double FifoLatency, double PriorityLatency, List<ProcessedItemDto> Recent) Stats()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            long nowSecond = now.Ticks / TimeSpan.TicksPerSecond;
            int total = 0;
            for (int i = 0; i < _rateCounts.Length; i++)
                if (nowSecond - _rateStamps[i] is >= 1 and <= 5) total += _rateCounts[i]; // last 5 complete seconds
            double perSecond = total / 5.0;
            return (_processed, _priorityProcessed, Math.Round(perSecond, 1), Math.Round(_fifoLatencyEwma, 1),
                Math.Round(_priorityLatencyEwma, 1), _recent.ToList());
        }
    }
}
