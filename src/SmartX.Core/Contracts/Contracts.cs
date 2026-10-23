using System.Text.Json;
using System.Text.Json.Serialization;
using SmartX.Core.Deployment;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;
using SmartX.Core.Topology;

namespace SmartX.Core.Contracts;

/// <summary>One JSON configuration shared by the API, the Blazor client and the device simulator.</summary>
public static class SmartXJson
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions o)
    {
        o.Converters.Add(new JsonStringEnumConverter());
        o.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
        return o;
    }
}

// ───────────────────────────── Gateway ─────────────────────────────
public sealed record PillarDto(string Key, string Title, string Description, string Route, bool Enabled, string Part, string Icon);

public sealed record GatewayStatusDto(
    IReadOnlyList<PillarDto> Pillars,
    int DeviceCount,
    int Online,
    int Stale,
    int Offline,
    long PacketsProcessed,
    int FifoDepth,
    int PriorityDepth,
    DateTime ServerTimeUtc,
    string Version);

// ───────────────────────────── Part 1: devices & ingestion ─────────────────────────────
public sealed record DeviceDto(
    string Mac,
    string DisplayName,
    SensorCategory Category,
    string Metric,
    string MetricLabel,
    string Unit,
    ValueKind Kind,
    string NodeId,
    string LocationPath,
    string Zone,
    string FacilityId,
    DeviceStatus Status,
    DateTime RegisteredUtc,
    DateTime? LastSeenUtc,
    double? LastValue,
    string LastDisplay,
    Severity Severity,
    bool? ActuatorOn,
    int AttachmentCount,
    double AnomalyScore,
    IReadOnlyList<string> ActiveAlerts);

public sealed class RegisterSensorRequest
{
    public string Mac { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public SensorCategory Category { get; set; }
    public string Metric { get; set; } = "";
    public string NodeId { get; set; } = "";
}

public sealed record RegisterSensorResponse(DeviceDto Device, PathValidationResult PathValidation);

public sealed record ApiError(string Title, IReadOnlyDictionary<string, string[]> Errors);

public sealed record DeploymentNodeDto(
    string Id,
    string Name,
    DeploymentLevel Level,
    int PowerWatts,
    bool Enabled,
    int DeviceCount,
    IReadOnlyList<DeploymentNodeDto> Children);

public sealed record DeploymentOverviewDto(IReadOnlyList<DeploymentNodeDto> Facilities, TreeValidationResult Validation);

public sealed class AddDeploymentNodeRequest
{
    public string ParentId { get; set; } = "";
    public string Name { get; set; } = "";
    public int PowerWatts { get; set; }
}

public sealed record AttachmentDto(
    Guid Id,
    string Mac,
    string FileName,
    AttachmentKind Kind,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateTime UploadedUtc,
    string Encryption,
    ConfigProfileValidator.Result? ConfigValidation);

/// <summary>Generic ingest body - bound per T by the typed endpoints, so the value is never boxed.</summary>
public sealed record TelemetryRequest<T>(string Mac, string Metric, T Value, DateTime? TimestampUtc = null) where T : struct;

/// <summary>Offline backfill: a device uploads several bursts (rows) of different lengths → jagged array.</summary>
public sealed record BatchTelemetryRequest(string Mac, string Metric, float[][] Batches, DateTime StartUtc, int IntervalMs);

public sealed record IngestAck(bool Accepted, string Lane, Severity Severity, long Sequence, string Message);

public sealed record BatchAck(bool Accepted, int Rows, int Readings, int[] RowLengths, string Message);

public sealed record MeterView(string MeterId, int Watts, double KilowattHours, int Sources);

public sealed record AggregateDto(MeterView A, MeterView B, MeterView Sum, MeterView Delta, MeterView AveragePerMeter, bool AIsHigher, bool SumOverBreakerLimit, string Expression);

public sealed record PipelineStatsDto(
    int RowSize,
    int[] FloatRows,
    int[] IntRows,
    int[] BoolRows,
    int FloatLedger,
    int IntLedger,
    int BoolLedger,
    long TotalAppended,
    long Flushes);

// ───────────────────────────── Part 1: engagement (Anomaly Radar) ─────────────────────────────
public sealed record HeartbeatDto(string Mac, string Name, string Zone, double SecondsSinceSeen, DeviceStatus Status, string LastDisplay, double AnomalyScore, Severity Severity);

public sealed record SpikeDto(string Mac, string Name, string Metric, string Display, double ZScore, DateTime AtUtc, string Kind);

public enum IssueStatus : byte { Open = 0, Acknowledged = 1, Resolved = 2 }

public sealed record IssueDto(int Id, string Mac, string DeviceName, string Title, Severity Severity, IssueStatus Status, string Source, DateTime CreatedUtc, DateTime? AcknowledgedUtc, DateTime? ResolvedUtc);

public sealed class ReportIssueRequest
{
    public string Mac { get; set; } = "";
    public string Title { get; set; } = "";
    public Severity Severity { get; set; } = Severity.Warning;
}

public sealed record RadarDto(
    IReadOnlyList<string> RowLabels,
    IReadOnlyList<string> ColumnLabels,
    double?[][] Cells,
    int BucketSeconds,
    IReadOnlyList<HeartbeatDto> Heartbeats,
    IReadOnlyList<SpikeDto> Spikes,
    IReadOnlyList<IssueDto> Issues,
    int HealthScore,
    double MeanTimeToAcknowledgeSec,
    int ResolvedCount,
    int TriageStreak,
    IReadOnlyList<string> Badges);

// ───────────────────────────── Part 2: real-time stream ─────────────────────────────
public sealed record ProcessedItemDto(long Sequence, string Mac, string DeviceName, string Metric, string Display, string Lane, double LatencyMs, DateTime ProcessedUtc, Severity Severity);

public sealed record AlertDto(long Id, string Mac, string DeviceName, string Code, Severity Severity, string Message, DateTime RaisedUtc, bool Active);

public sealed record StreamStatsDto(
    int FifoDepth,
    int PriorityDepth,
    long Processed,
    long PriorityProcessed,
    double ProcessedPerSecond,
    double AvgFifoLatencyMs,
    double AvgPriorityLatencyMs,
    long DuplicateAlertsSuppressed,
    IReadOnlyList<string> DisconnectedNodes,
    IReadOnlyList<string> ActiveErrorStates,
    IReadOnlyList<ProcessedItemDto> Recent,
    IReadOnlyList<AlertDto> Alerts);

public enum CommandAction : byte { On = 0, Off = 1, Toggle = 2 }

public enum CommandScope : byte { Device = 0, AllOfMetric = 1 }

public sealed class CommandRequest
{
    public string? Mac { get; set; }
    public CommandAction Action { get; set; }
    public CommandScope Scope { get; set; } = CommandScope.Device;
    public string? Metric { get; set; }
    public string Source { get; set; } = "Manual";
}

public sealed record CommandRecordDto(long Id, string Description, string Source, DateTime IssuedUtc, int TargetCount, bool Undone, IReadOnlyList<string> Changes);

public sealed record CommandStateDto(IReadOnlyList<CommandRecordDto> UndoStack, IReadOnlyList<CommandRecordDto> Log, IReadOnlyDictionary<string, bool> Actuators);

public sealed record TimelinePointDto(DateTime T, double V);

public sealed record DeviceTimelineDto(string Mac, string Name, string Unit, double NormalMin, double NormalMax, IReadOnlyList<TimelinePointDto> Points);

public sealed record SuggestionDto(
    string Id,
    string Title,
    string Reason,
    double Confidence,
    string Kind,
    string? Mac,
    CommandAction? Action,
    string Evidence);

public sealed record DeviceSearchResultDto(IReadOnlyList<DeviceDto> Devices, int Total, double ElapsedMs);

// ───────────────────────────── Part 3: topology & history ─────────────────────────────
public sealed record VertexDto(string Id, string Label, string Zone, double X, double Y, bool IsGateway, bool IsOnline, bool ForcedDown, int? Hops);

public sealed record EdgeDto(string A, string B, int RssiDbm, double Cost);

public sealed record TopologyDto(
    string GatewayId,
    IReadOnlyList<VertexDto> Vertices,
    IReadOnlyList<EdgeDto> Edges,
    IReadOnlyList<EdgeDto> MstEdges,
    double MstCost,
    double FullMeshCost,
    int Reachable,
    IReadOnlyList<string> UnreachableOnline,
    IReadOnlyList<string> Offline,
    IReadOnlyList<IReadOnlyList<string>> Components,
    IReadOnlyList<string> BfsOrder,
    IReadOnlyList<HopAlternativeDto> Healing,
    IReadOnlyList<FacilityDto> Facilities,
    double ComputeMs);

public sealed record FacilityDto(string Id, string Name);

public sealed record HopAlternativeDto(string DownNode, string DownLabel, IReadOnlyList<string> OrphanedNeighbours, IReadOnlyList<string> SuggestedHops);

public sealed record RouteDto(
    string Target,
    string TargetLabel,
    RouteResult Mst,
    RouteResult FewestHops,
    RouteResult CheapestEnergy,
    IReadOnlyList<string> PathLabels,
    IReadOnlyList<HopAlternativeDto> Healing);

public sealed record HistoryQueryResultDto(
    string Mac,
    string Name,
    string Unit,
    DateTime FromUtc,
    DateTime ToUtc,
    int Count,
    int NodesVisited,
    int TreeSize,
    int TreeHeight,
    long Rotations,
    double ElapsedMicroseconds,
    double? Min,
    double? Max,
    double? Average,
    IReadOnlyList<TimelinePointDto> Chart,
    IReadOnlyList<TimelinePointDto> Rows);

public sealed record InsightPatternDto(string Type, string When, string Then, int Count, double Probability);

public sealed record InsightsDto(IReadOnlyList<SuggestionDto> Suggestions, IReadOnlyList<InsightPatternDto> Patterns, int LearnedActions);

public sealed record UploadResultDto(IReadOnlyList<AttachmentDto> Saved, IReadOnlyList<string> Rejected);
