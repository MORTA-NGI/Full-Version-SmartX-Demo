using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SmartX.Api.Security;
using SmartX.Api.Services;
using SmartX.Core.Contracts;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Endpoints;

/// <summary>Part 1 - gateway status, sensor registration, telemetry ingestion, attachments, engagement.</summary>
public static class IngestionEndpoints
{
    public static void MapIngestionEndpoints(this IEndpointRouteBuilder app)
    {
        // ───────── gateway ─────────
        app.MapGet("/api/gateway/status", (Microsoft.Extensions.Options.IOptions<GatewayOptions> opts, DeviceRegistry registry, StreamProcessor processor) =>
        {
            int active = Math.Clamp(opts.Value.ActivePillars, 1, 3);
            var pillars = new List<PillarDto>
            {
                new("ingestion", "Sensor Data Ingestion & Telemetry", "Register ESP32 sensors, attach configs/logs and watch the live Anomaly Radar.", "/ingestion", true, "Part 1", "📡"),
                new("stream", "Real-Time Command Stream & History", "Priority alert queues, manual overrides with undo, and predictive suggested actions.", "/stream", active >= 2, "Part 2", "⚡"),
                new("topology", "Network Topology & Mesh Routing", "Mesh graph, BFS reachability, Prim's MST routing and AVL-indexed history.", "/topology", active >= 3, "PoE", "🕸️"),
            };
            var devices = registry.Snapshot();
            var (fifo, prio) = processor.Depths();
            return Results.Ok(new GatewayStatusDto(pillars, devices.Count,
                devices.Count(d => d.Status == DeviceStatus.Online), devices.Count(d => d.Status == DeviceStatus.Stale),
                devices.Count(d => d.Status == DeviceStatus.Offline), processor.Processed, fifo, prio, DateTime.UtcNow, "1.0.0"));
        });

        app.MapGet("/api/metrics", () => Results.Ok(MetricCatalog.All));

        // ───────── devices ─────────
        var devices = app.MapGroup("/api/devices");

        devices.MapGet("/", (string? query, SensorCategory? category, string? state, bool? record,
            DeviceRegistry registry, AlertService alerts, InsightEngine insights) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IEnumerable<DeviceDto> q = registry.Snapshot(alerts.ActiveFor);
            if (category is not null) q = q.Where(d => d.Category == category);
            if (!string.IsNullOrWhiteSpace(state))
            {
                q = state.ToLowerInvariant() switch
                {
                    "critical" => q.Where(d => d.Severity == Severity.Critical || d.ActiveAlerts.Any(a => a.StartsWith("CRITICAL"))),
                    "warning" => q.Where(d => d.Severity == Severity.Warning || d.ActiveAlerts.Any(a => a.StartsWith("WARNING") || a == "SPIKE")),
                    "alerting" => q.Where(d => d.ActiveAlerts.Count > 0),
                    "disconnected" => q.Where(d => d.Status == DeviceStatus.Offline),
                    "healthy" => q.Where(d => d.Status == DeviceStatus.Online && d.ActiveAlerts.Count == 0),
                    _ => q
                };
            }
            if (!string.IsNullOrWhiteSpace(query))
            {
                var term = query.Trim();
                q = q.Where(d => d.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                                 || d.Mac.Contains(term, StringComparison.OrdinalIgnoreCase)
                                 || d.LocationPath.Contains(term, StringComparison.OrdinalIgnoreCase)
                                 || d.MetricLabel.Contains(term, StringComparison.OrdinalIgnoreCase));
            }
            var list = q.OrderBy(d => d.Zone).ThenBy(d => d.DisplayName).ToList();
            sw.Stop();
            if (record == true && !string.IsNullOrWhiteSpace(query)) insights.RecordSearch(query, list, alerts.ActiveConditions());
            return Results.Ok(new DeviceSearchResultDto(list, list.Count, Math.Round(sw.Elapsed.TotalMilliseconds, 3)));
        });

        devices.MapGet("/{mac}", (string mac, DeviceRegistry registry, AlertService alerts) =>
            MacAddress.TryNormalize(mac, out var m) && registry.Get(m, alerts.ActiveFor) is { } d ? Results.Ok(d) : Results.NotFound());

        devices.MapPost("/", (RegisterSensorRequest req, RegistrationService registration) =>
        {
            var (result, errors) = registration.Register(req);
            return result is null
                ? Results.ValidationProblem(errors, title: "Sensor registration failed")
                : Results.Created($"/api/devices/{result.Device.Mac}", result);
        });

        devices.MapPost("/{mac}/inspect", (string mac, InsightEngine insights, AlertService alerts) =>
        {
            if (!MacAddress.TryNormalize(mac, out var m)) return Results.BadRequest();
            insights.RecordInspect(m, alerts.ActiveConditions());
            return Results.NoContent();
        });

        devices.MapGet("/{mac}/timeline", (string mac, int? minutes, DeviceRegistry registry, HistoryService history) =>
        {
            if (!MacAddress.TryNormalize(mac, out var m) || !registry.TryGet(m, out var d)) return Results.NotFound();
            var since = DateTime.UtcNow.AddMinutes(-Math.Clamp(minutes ?? 10, 1, 15));
            return Results.Ok(new DeviceTimelineDto(m, d.DisplayName, d.Metric.Unit, d.Metric.NormalMin, d.Metric.NormalMax, history.Timeline(m, since)));
        });

        // ───────── attachments (streamed multipart → AES-GCM → disk) ─────────
        devices.MapGet("/{mac}/attachments", (string mac, AttachmentStore store) =>
            MacAddress.TryNormalize(mac, out var m) ? Results.Ok(store.List(m)) : Results.BadRequest());

        devices.MapPost("/{mac}/attachments", async (string mac, HttpRequest request, DeviceRegistry registry, AttachmentStore store, CancellationToken ct) =>
        {
            if (!MacAddress.TryNormalize(mac, out var m) || !registry.Contains(m)) return Results.NotFound();
            if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
                !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Expected multipart/form-data." });

            var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
            if (string.IsNullOrEmpty(boundary)) return Results.BadRequest(new { error = "Missing multipart boundary." });

            var reader = new MultipartReader(boundary, request.Body);
            var saved = new List<AttachmentDto>();
            var rejected = new List<string>();
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd) || !cd.IsFileDisposition()) continue;
                var fileName = cd.FileNameStar.HasValue ? cd.FileNameStar.Value! : cd.FileName.Value ?? "upload";
                try
                {
                    saved.Add(await store.SaveAsync(m, fileName, section.Body, ct)); // section.Body is a forward-only stream
                }
                catch (Exception ex) when (ex is InvalidDataException or ChunkedAesGcm.FileTooLargeException)
                {
                    rejected.Add($"{fileName}: {ex.Message}");
                }
            }
            registry.AdjustAttachmentCount(m, saved.Count);
            if (saved.Count == 0) return Results.BadRequest(new { error = rejected.Count > 0 ? string.Join(" | ", rejected) : "No file found in request.", rejected });
            return Results.Ok(new UploadResultDto(saved, rejected));
        }).DisableAntiforgery();

        devices.MapGet("/{mac}/attachments/{id:guid}", async (string mac, Guid id, HttpContext ctx, AttachmentStore store, CancellationToken ct) =>
        {
            if (!MacAddress.TryNormalize(mac, out var m) || store.Find(m, id) is not { } meta) return Results.NotFound();
            ctx.Response.ContentType = meta.ContentType;
            ctx.Response.Headers.ContentDisposition = $"inline; filename=\"{meta.FileName}\"";
            await store.DecryptToAsync(m, id, ctx.Response.Body, ct);
            return Results.Empty;
        });

        devices.MapDelete("/{mac}/attachments/{id:guid}", (string mac, Guid id, AttachmentStore store, DeviceRegistry registry) =>
        {
            if (!MacAddress.TryNormalize(mac, out var m) || !store.Delete(m, id)) return Results.NotFound();
            registry.AdjustAttachmentCount(m, -1);
            return Results.NoContent();
        });

        // ───────── deployment tree ─────────
        app.MapGet("/api/deployment", (DeploymentService deployment, DeviceRegistry registry) =>
            Results.Ok(deployment.Overview(registry.CountsByNode())));

        app.MapGet("/api/deployment/validate/{nodeId}", (string nodeId, DeploymentService deployment) =>
            Results.Ok(deployment.ValidatePath(nodeId)));

        app.MapPost("/api/deployment/nodes", (AddDeploymentNodeRequest req, DeploymentService deployment, DeviceRegistry registry) =>
        {
            var (node, error) = deployment.AddChild(req);
            return node is null
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["ParentId"] = [error!] })
                : Results.Ok(deployment.Overview(registry.CountsByNode()));
        });

        // ───────── telemetry ingestion (one typed endpoint per T → no boxing) ─────────
        var telemetry = app.MapGroup("/api/telemetry");

        telemetry.MapPost("/float", (TelemetryRequest<float> req, IngestionGateway gateway) =>
            ToResult(gateway.Ingest(req, ValueKind.Float, static (p, s) => TelemetryEnvelope.From(p, s))));

        telemetry.MapPost("/int", (TelemetryRequest<int> req, IngestionGateway gateway) =>
            ToResult(gateway.Ingest(req, ValueKind.Int, static (p, s) => TelemetryEnvelope.From(p, s))));

        telemetry.MapPost("/bool", (TelemetryRequest<bool> req, IngestionGateway gateway) =>
            ToResult(gateway.Ingest(req, ValueKind.Bool, static (p, s) => TelemetryEnvelope.From(p, s))));

        telemetry.MapPost("/batch", (BatchTelemetryRequest req, IngestionGateway gateway) =>
        {
            var (ack, error) = gateway.IngestBatch(req);
            return ack is null ? Results.BadRequest(new { error }) : Results.Accepted(value: ack);
        });

        telemetry.MapGet("/pipeline", (TelemetryHub hub) => Results.Ok(hub.Stats()));

        // Operator overloading demo on live data: Meter3 = Meter1 + Meter2.
        telemetry.MapGet("/aggregate", (string a, string b, DeviceRegistry registry, HistoryService history) =>
        {
            if (!MacAddress.TryNormalize(a, out var ma) || !MacAddress.TryNormalize(b, out var mb) ||
                !registry.TryGet(ma, out var da) || !registry.TryGet(mb, out var db))
                return Results.NotFound(new { error = "Choose two registered meters." });
            if (da.Metric.Key != "power_w" || db.Metric.Key != "power_w")
                return Results.BadRequest(new { error = "Aggregation needs two power meters (power_w)." });

            var since = DateTime.UtcNow.AddHours(-24);
            var meter1 = new SmartMeterReading(da.DisplayName, (int)(da.LastValue ?? 0), Math.Round((history.AverageSince(ma, since) ?? 0) * 24 / 1000, 2), da.LastSeenUtc ?? DateTime.UtcNow);
            var meter2 = new SmartMeterReading(db.DisplayName, (int)(db.LastValue ?? 0), Math.Round((history.AverageSince(mb, since) ?? 0) * 24 / 1000, 2), db.LastSeenUtc ?? DateTime.UtcNow);

            SmartMeterReading meter3 = meter1 + meter2;   // overloaded +
            SmartMeterReading delta = meter1 - meter2;    // overloaded -
            SmartMeterReading average = meter3 / 2;       // overloaded /
            bool aHigher = meter1 > meter2;               // overloaded >
            bool overLimit = meter3 > 3500;               // overloaded > (reading, int)

            static MeterView V(SmartMeterReading r) => new(r.MeterId, r.Watts, Math.Round(r.KilowattHours, 2), r.Sources);
            return Results.Ok(new AggregateDto(V(meter1), V(meter2), V(meter3), V(delta), V(average), aHigher, overLimit,
                $"{meter1.MeterId} ({meter1.Watts} W) + {meter2.MeterId} ({meter2.Watts} W) = {meter3.Watts} W"));
        });

        // ───────── engagement: Anomaly Radar + issues ─────────
        var engagement = app.MapGroup("/api/engagement");
        engagement.MapGet("/radar", (EngagementService svc) => Results.Ok(svc.Radar()));
        engagement.MapPost("/issues", (ReportIssueRequest req, EngagementService svc) =>
        {
            var (issue, error) = svc.Report(req);
            return issue is null ? Results.ValidationProblem(new Dictionary<string, string[]> { ["Title"] = [error!] }) : Results.Ok(issue);
        });
        engagement.MapPost("/issues/{id:int}/ack", (int id, EngagementService svc) =>
            svc.Transition(id, IssueStatus.Acknowledged) is { } i ? Results.Ok(i) : Results.NotFound());
        engagement.MapPost("/issues/{id:int}/resolve", (int id, EngagementService svc) =>
            svc.Transition(id, IssueStatus.Resolved) is { } i ? Results.Ok(i) : Results.NotFound());
    }

    private static IResult ToResult((IngestAck? Ack, string? Error) r) =>
        r.Ack is null ? Results.BadRequest(new { error = r.Error }) : Results.Accepted(value: r.Ack);
}
