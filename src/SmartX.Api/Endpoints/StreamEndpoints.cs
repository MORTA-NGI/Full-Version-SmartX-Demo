using SmartX.Api.Services;
using SmartX.Core.Contracts;
using SmartX.Core.Devices;

namespace SmartX.Api.Endpoints;

/// <summary>Part 2 - real-time command stream, queues, undo stack, alert sets and predictive insights.</summary>
public static class StreamEndpoints
{
    public static void MapStreamEndpoints(this IEndpointRouteBuilder app)
    {
        var stream = app.MapGroup("/api/stream");

        stream.MapGet("/stats", (StreamProcessor processor, AlertService alerts) =>
        {
            var (fifo, prio) = processor.Depths();
            var s = processor.Stats();
            var (disconnected, active, feed, dups) = alerts.Snapshot();
            return Results.Ok(new StreamStatsDto(fifo, prio, s.Processed, s.PriorityProcessed, s.PerSecond,
                s.FifoLatency, s.PriorityLatency, dups, disconnected, active, s.Recent, feed));
        });

        stream.MapPost("/simulate/critical", (string? device, IngestionGateway gateway) =>
        {
            var (ack, error) = gateway.InjectCritical(device);
            return ack is null ? Results.BadRequest(new { error }) : Results.Accepted(value: ack);
        });

        stream.MapPost("/simulate/flood", (int? count, IngestionGateway gateway) =>
            Results.Accepted(value: new { queued = gateway.Flood(count ?? 3000) }));

        var commands = app.MapGroup("/api/commands");

        commands.MapGet("/", (CommandService svc) => Results.Ok(svc.State()));

        commands.MapPost("/", (CommandRequest req, CommandService svc) =>
        {
            var (record, error) = svc.Issue(req);
            return record is null ? Results.BadRequest(new { error }) : Results.Ok(record);
        });

        commands.MapPost("/undo", (CommandService svc) =>
            svc.Undo() is { } undone ? Results.Ok(undone) : Results.Conflict(new { error = "Nothing to undo - the command stack is empty." }));

        // Polled by the device simulator: desired actuator states (what the gateway wants the relays to do).
        commands.MapGet("/actuators", (DeviceRegistry registry) => Results.Ok(registry.ActuatorStates()));

        app.MapGet("/api/insights", (InsightEngine insights) =>
            Results.Ok(new InsightsDto(insights.Suggest(), insights.Patterns(), insights.LearnedActions)));
    }
}
