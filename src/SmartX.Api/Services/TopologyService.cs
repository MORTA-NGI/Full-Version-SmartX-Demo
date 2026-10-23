using System.Diagnostics;
using SmartX.Core.Contracts;
using SmartX.Core.Devices;
using SmartX.Core.Topology;

namespace SmartX.Api.Services;

/// <summary>
/// Builds the mesh graph for a facility from the live registry and runs the routing algorithms.
/// Vertices = ESP32 nodes + the central gateway; edges = radio links between nodes within range,
/// weighted by estimated transmit energy (cost grows with distance², like real RF path loss).
/// </summary>
public sealed class TopologyService(DeviceRegistry registry, DeploymentService deployment)
{
    private const double Width = 1000, Height = 620;

    private readonly HashSet<string> _forcedDown = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public static string GatewayId(string facilityId) => $"GW-{facilityId}";

    /// <summary>Simulate a hardware failure (or repair) on a node to watch the mesh heal.</summary>
    public bool ToggleForcedDown(string mac)
    {
        lock (_gate)
        {
            if (_forcedDown.Remove(mac)) return false;
            _forcedDown.Add(mac);
            return true;
        }
    }

    public TopologyDto Overview(string? facilityId)
    {
        var sw = Stopwatch.StartNew();
        var facilities = deployment.Facilities().Select(f => new FacilityDto(f.Id, f.Name)).ToList();
        facilityId ??= facilities.FirstOrDefault()?.Id ?? "";
        var (graph, devices) = Build(facilityId, respectOnline: true);
        string gw = GatewayId(facilityId);

        var bfs = MeshAlgorithms.BreadthFirst(graph, gw);
        var mst = MeshAlgorithms.PrimMst(graph, gw);
        var components = MeshAlgorithms.ConnectedComponents(graph);
        var healing = Healing(facilityId, graph, mst, devices);
        sw.Stop();

        HashSet<string> forced;
        lock (_gate) forced = [.. _forcedDown];

        var vertices = graph.Vertices.Select(v => new VertexDto(v.Id, v.Label, v.Zone, Math.Round(v.X, 1), Math.Round(v.Y, 1),
            v.IsGateway, v.IsOnline, forced.Contains(v.Id), bfs.HopCount.TryGetValue(v.Id, out var h) ? h : null)).ToList();

        return new TopologyDto(gw, vertices,
            graph.Edges.Select(ToDto).ToList(),
            mst.Edges.Select(ToDto).ToList(),
            mst.TotalCost, mst.FullMeshCost,
            bfs.HopCount.Count - 1,
            bfs.UnreachableOnline, bfs.Offline,
            components.Select(c => (IReadOnlyList<string>)c).ToList(),
            bfs.VisitOrder, healing, facilities, Math.Round(sw.Elapsed.TotalMilliseconds, 2));
    }

    public (RouteDto? Route, string? Error) Route(string query)
    {
        var all = registry.Snapshot();
        DeviceDto? target = MacAddress.TryNormalize(query, out var mac)
            ? all.FirstOrDefault(d => d.Mac == mac)
            : all.FirstOrDefault(d => d.DisplayName.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? all.FirstOrDefault(d => d.DisplayName.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target is null) return (null, $"No sensor matches '{query}'.");

        var (graph, devices) = Build(target.FacilityId, respectOnline: true);
        string gw = GatewayId(target.FacilityId);
        var names = graph.Vertices.ToDictionary(v => v.Id, v => v.Label, StringComparer.Ordinal);

        var mst = MeshAlgorithms.PrimMst(graph, gw);
        var mstRoute = Reverse(MeshAlgorithms.RouteOverTree(mst.Edges, gw, target.Mac));
        var bfs = MeshAlgorithms.BreadthFirst(graph, gw);
        var hopRoute = Reverse(MeshAlgorithms.RouteFromBfs(graph, bfs, target.Mac));
        var cheapest = Reverse(MeshAlgorithms.Dijkstra(graph, gw, target.Mac));

        if (graph.TryGetVertex(target.Mac, out var tv) && !tv.IsOnline)
        {
            mstRoute = mstRoute with { Found = false, Message = $"{target.DisplayName} is offline - it cannot transmit." };
        }

        var labels = mstRoute.Path.Select(id => names.GetValueOrDefault(id, id)).ToList();
        var healing = Healing(target.FacilityId, graph, mst, devices)
            .Where(hh => hh.OrphanedNeighbours.Count > 0)
            .ToList();

        return (new RouteDto(target.Mac, target.DisplayName, mstRoute, hopRoute, cheapest, labels, healing), null);
    }

    /// <summary>
    /// Self-healing analysis: for each failed node, which neighbours used to route THROUGH it (on the healthy
    /// network's MST) and where do they hop now?
    /// </summary>
    private List<HopAlternativeDto> Healing(string facilityId, MeshGraph live, MstResult liveMst, List<DeviceDto> devices)
    {
        var down = live.Vertices.Where(v => !v.IsOnline).ToList();
        if (down.Count == 0) return [];

        var (healthy, _) = Build(facilityId, respectOnline: false);
        string gw = GatewayId(facilityId);
        var healthyMst = MeshAlgorithms.PrimMst(healthy, gw);
        var names = live.Vertices.ToDictionary(v => v.Id, v => v.Label, StringComparer.Ordinal);
        var result = new List<HopAlternativeDto>();

        foreach (var dead in down)
        {
            var orphans = new List<string>();
            var hops = new List<string>();
            foreach (var edge in live.Neighbours(dead.Id))
            {
                var n = edge.Other(dead.Id);
                if (!live.TryGetVertex(n, out var nv) || !nv.IsOnline || nv.IsGateway) continue;
                var before = MeshAlgorithms.RouteOverTree(healthyMst.Edges, n, gw);
                if (!before.Found || before.Path.Count < 2 || before.Path[1] != dead.Id) continue; // didn't depend on it

                orphans.Add(names[n]);
                var now = MeshAlgorithms.RouteOverTree(liveMst.Edges, n, gw);
                hops.Add(now.Found && now.Path.Count > 1
                    ? $"{names[n]} now hops via {names.GetValueOrDefault(now.Path[1], now.Path[1])} ({now.Hops} hops, {now.Cost} mJ)"
                    : $"{names[n]} is isolated - no live neighbour can reach the gateway");
            }
            result.Add(new HopAlternativeDto(dead.Id, dead.Label, orphans, hops));
        }
        return result;
    }

    private (MeshGraph Graph, List<DeviceDto> Devices) Build(string facilityId, bool respectOnline)
    {
        var devices = registry.Snapshot()
            .Where(d => d.FacilityId == facilityId)
            .OrderBy(d => d.Zone, StringComparer.Ordinal).ThenBy(d => d.DisplayName, StringComparer.Ordinal)
            .ToList();

        HashSet<string> forced;
        lock (_gate) forced = [.. _forcedDown];

        var graph = new MeshGraph();
        string gw = GatewayId(facilityId);
        graph.AddVertex(new MeshVertex { Id = gw, Label = "Gateway", Zone = "Core", X = 70, Y = Height / 2, IsGateway = true });

        // ── deterministic cluster layout: one cluster per sub-zone ──
        var zones = devices.Select(d => d.Zone).Distinct().ToList();
        int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(zones.Count * 1.4)));
        int rows = Math.Max(1, (int)Math.Ceiling(zones.Count / (double)cols));
        double cellW = (Width - 190) / cols, cellH = (Height - 70) / rows;
        double radius = Math.Min(cellW, cellH) * 0.3;

        for (int z = 0; z < zones.Count; z++)
        {
            double cx = 180 + cellW * (z % cols + 0.5), cy = 40 + cellH * (z / cols + 0.5);
            var members = devices.Where(d => d.Zone == zones[z]).ToList();
            for (int j = 0; j < members.Count; j++)
            {
                double angle = 2 * Math.PI * j / members.Count + z * 0.7;
                double r = members.Count == 1 ? 0 : radius;
                var d = members[j];
                graph.AddVertex(new MeshVertex
                {
                    Id = d.Mac, Label = d.DisplayName, Zone = d.Zone,
                    X = cx + r * Math.Cos(angle), Y = cy + r * Math.Sin(angle),
                    IsOnline = !respectOnline || (d.Status != DeviceStatus.Offline && !forced.Contains(d.Mac))
                });
            }
        }

        // ── radio links: every pair within range ──
        var vs = graph.Vertices.ToList();
        double range = Math.Max(cellW, cellH) * 0.72;
        for (int i = 0; i < vs.Count; i++)
            for (int j = i + 1; j < vs.Count; j++)
            {
                double dist = Distance(vs[i], vs[j]);
                if (dist <= range) Link(graph, vs[i], vs[j], dist);
            }

        // ── guarantee one connected mesh: bridge every island to the gateway's component ──
        bool merged = true;
        while (merged)
        {
            merged = false;
            var comps = AllComponents(graph);
            var main = comps.First(c => c.Contains(gw));
            foreach (var island in comps.Where(c => !c.Contains(gw)))
            {
                (MeshVertex A, MeshVertex B, double D) best = (null!, null!, double.MaxValue);
                foreach (var a in island)
                    foreach (var b in main)
                    {
                        graph.TryGetVertex(a, out var va);
                        graph.TryGetVertex(b, out var vb);
                        double dd = Distance(va, vb);
                        if (dd < best.D) best = (va, vb, dd);
                    }
                Link(graph, best.A, best.B, best.D);
                merged = true;
                break; // recompute components after each bridge
            }
        }
        return (graph, devices);
    }

    private static List<HashSet<string>> AllComponents(MeshGraph g)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<HashSet<string>>();
        foreach (var v in g.Vertices)
        {
            if (!seen.Add(v.Id)) continue;
            var comp = new HashSet<string>(StringComparer.Ordinal) { v.Id };
            var stack = new Stack<string>([v.Id]);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                foreach (var e in g.Neighbours(cur))
                {
                    var n = e.Other(cur);
                    if (seen.Add(n)) { comp.Add(n); stack.Push(n); }
                }
            }
            result.Add(comp);
        }
        return result;
    }

    private static void Link(MeshGraph g, MeshVertex a, MeshVertex b, double dist)
    {
        double meters = Math.Max(1, dist / 10);
        int rssi = (int)Math.Round(-40 - 25 * Math.Log10(meters));
        double cost = Math.Round(1 + meters * meters / 40, 2); // mJ per packet, ∝ distance² path loss
        g.AddEdge(a.Id, b.Id, rssi, cost);
    }

    private static double Distance(MeshVertex a, MeshVertex b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static EdgeDto ToDto(MeshEdge e) => new(e.A, e.B, e.RssiDbm, e.Cost);

    /// <summary>Algorithms produce gateway → node; telemetry travels node → gateway, so flip for display.</summary>
    private static RouteResult Reverse(RouteResult r) => r with { Path = r.Path.Reverse().ToList() };
}
