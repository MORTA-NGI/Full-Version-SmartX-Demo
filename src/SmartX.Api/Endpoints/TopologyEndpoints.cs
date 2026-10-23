using SmartX.Api.Services;
using SmartX.Core.Devices;

namespace SmartX.Api.Endpoints;

/// <summary>PoE - mesh topology (graph, BFS/DFS, Prim's MST) and AVL-indexed historical queries.</summary>
public static class TopologyEndpoints
{
    public static void MapTopologyEndpoints(this IEndpointRouteBuilder app)
    {
        var topo = app.MapGroup("/api/topology");

        topo.MapGet("/", (string? facility, TopologyService svc) => Results.Ok(svc.Overview(facility)));

        topo.MapGet("/route", (string query, TopologyService svc) =>
        {
            var (route, error) = svc.Route(query);
            return route is null ? Results.NotFound(new { error }) : Results.Ok(route);
        });

        topo.MapPost("/nodes/{mac}/toggle", (string mac, TopologyService svc) =>
            MacAddress.TryNormalize(mac, out var m)
                ? Results.Ok(new { mac = m, forcedDown = svc.ToggleForcedDown(m) })
                : Results.BadRequest());

        app.MapGet("/api/history/{mac}", (string mac, DateTime? from, DateTime? to, DeviceRegistry registry, HistoryService history) =>
        {
            if (!MacAddress.TryNormalize(mac, out var m) || !registry.TryGet(m, out var d)) return Results.NotFound();
            var toUtc = (to ?? DateTime.UtcNow).ToUniversalTime();
            var fromUtc = (from ?? toUtc.AddHours(-2)).ToUniversalTime();
            if (fromUtc > toUtc) (fromUtc, toUtc) = (toUtc, fromUtc);
            return Results.Ok(history.Query(m, d.DisplayName, d.Metric.Unit, fromUtc, toUtc));
        });

        app.MapGet("/api/history/{mac}/coverage", (string mac, HistoryService history) =>
        {
            if (!MacAddress.TryNormalize(mac, out var m)) return Results.BadRequest();
            var (oldest, newest, count) = history.Coverage(m);
            return Results.Ok(new { oldest, newest, count });
        });
    }
}
