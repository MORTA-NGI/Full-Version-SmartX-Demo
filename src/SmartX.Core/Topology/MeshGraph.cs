using SmartX.Core.Collections;

namespace SmartX.Core.Topology;

/// <summary>A vertex: one ESP32 sensor node (or the central gateway).</summary>
public sealed class MeshVertex
{
    public required string Id { get; init; }          // MAC address or gateway id
    public required string Label { get; init; }
    public string Zone { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public bool IsGateway { get; init; }
    public bool IsOnline { get; set; } = true;
}

/// <summary>An undirected wireless link. <see cref="Cost"/> is the estimated transmit energy per packet (mJ).</summary>
public sealed record MeshEdge(string A, string B, int RssiDbm, double Cost)
{
    public string Other(string id) => id == A ? B : A;
}

/// <summary>
/// Undirected weighted graph modelling the IoT mesh (Zigbee / Thread / ESP-NOW style).
/// Stored as an adjacency list: <c>Dictionary&lt;vertexId, List&lt;MeshEdge&gt;&gt;</c> → O(1) vertex lookup and
/// O(degree) neighbour iteration, which is ideal for sparse wireless meshes.
/// </summary>
public sealed class MeshGraph
{
    private readonly Dictionary<string, MeshVertex> _vertices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<MeshEdge>> _adjacency = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MeshEdge> _edges = [];

    public IReadOnlyCollection<MeshVertex> Vertices => _vertices.Values;
    public IReadOnlyList<MeshEdge> Edges => _edges;
    public int VertexCount => _vertices.Count;

    public void AddVertex(MeshVertex v)
    {
        _vertices[v.Id] = v;
        _adjacency.TryAdd(v.Id, []);
    }

    public bool TryGetVertex(string id, out MeshVertex vertex) => _vertices.TryGetValue(id, out vertex!);

    public void AddEdge(string a, string b, int rssiDbm, double cost)
    {
        if (!_vertices.ContainsKey(a) || !_vertices.ContainsKey(b))
            throw new KeyNotFoundException("Both vertices must exist before linking them.");
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || HasEdge(a, b)) return;

        var edge = new MeshEdge(a, b, rssiDbm, cost);
        _edges.Add(edge);
        _adjacency[a].Add(edge);
        _adjacency[b].Add(edge);
    }

    public bool HasEdge(string a, string b) =>
        _adjacency.TryGetValue(a, out var list) && list.Exists(e => string.Equals(e.Other(a), b, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<MeshEdge> Neighbours(string id) =>
        _adjacency.TryGetValue(id, out var list) ? list : [];

}

/// <summary>Result of a breadth-first sweep from a gateway.</summary>
public sealed record BfsResult(
    string Source,
    IReadOnlyDictionary<string, int> HopCount,
    IReadOnlyDictionary<string, string?> Parent,
    IReadOnlyList<string> VisitOrder,
    IReadOnlyList<string> UnreachableOnline,
    IReadOnlyList<string> Offline);

public sealed record MstResult(
    IReadOnlyList<MeshEdge> Edges,
    double TotalCost,
    double FullMeshCost,
    int VerticesSpanned,
    IReadOnlyList<string> Excluded);

public sealed record RouteResult(
    bool Found,
    IReadOnlyList<string> Path,
    int Hops,
    double Cost,
    string Strategy,
    string? Message);

/// <summary>Graph algorithms over <see cref="MeshGraph"/>.</summary>
public static class MeshAlgorithms
{
    /// <summary>
    /// Breadth-First Search from <paramref name="source"/> (normally the gateway) that only travels through
    /// online nodes. Produces hop counts (BFS guarantees the minimum number of hops) and a parent map that
    /// can be walked back to rebuild any shortest-hop route. Complexity O(V + E).
    /// </summary>
    public static BfsResult BreadthFirst(MeshGraph graph, string source)
    {
        var hops = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var queue = new Queue<string>();

        if (graph.TryGetVertex(source, out var src) && src.IsOnline)
        {
            hops[source] = 0;
            parent[source] = null;
            queue.Enqueue(source);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            order.Add(current);
            foreach (var edge in graph.Neighbours(current))
            {
                var next = edge.Other(current);
                if (hops.ContainsKey(next)) continue;                      // already discovered
                if (!graph.TryGetVertex(next, out var v) || !v.IsOnline) continue; // can't hop through a dead node
                hops[next] = hops[current] + 1;
                parent[next] = current;
                queue.Enqueue(next);
            }
        }

        var offline = graph.Vertices.Where(v => !v.IsOnline).Select(v => v.Id).ToList();
        var unreachable = graph.Vertices.Where(v => v.IsOnline && !hops.ContainsKey(v.Id)).Select(v => v.Id).ToList();
        return new BfsResult(source, hops, parent, order, unreachable, offline);
    }

    /// <summary>
    /// Iterative Depth-First Search used to split the mesh into connected components ("subnets" / islands).
    /// After a failure an island that does not contain the gateway is cut off and needs a new hop.
    /// </summary>
    public static List<List<string>> ConnectedComponents(MeshGraph graph)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = new List<List<string>>();

        foreach (var start in graph.Vertices.Where(v => v.IsOnline))
        {
            if (!visited.Add(start.Id)) continue;
            var component = new List<string>();
            var stack = new Stack<string>();
            stack.Push(start.Id);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                component.Add(current);
                foreach (var edge in graph.Neighbours(current))
                {
                    var next = edge.Other(current);
                    if (graph.TryGetVertex(next, out var v) && v.IsOnline && visited.Add(next))
                        stack.Push(next);
                }
            }
            components.Add(component);
        }
        return components.OrderByDescending(c => c.Count).ToList();
    }

    /// <summary>
    /// Prim's algorithm with a binary min-heap: grows a tree from the gateway, always adding the cheapest edge
    /// that reaches a new online vertex. The result connects every reachable node with the minimum total
    /// transmit energy - the most power-efficient backbone for telemetry to hop to the server.
    /// Complexity O(E log V).
    /// </summary>
    public static MstResult PrimMst(MeshGraph graph, string root)
    {
        var inTree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mst = new List<MeshEdge>();
        var heap = new MinHeap<MeshEdge, double>(graph.Edges.Count + 1);
        double total = 0;

        if (graph.TryGetVertex(root, out var r) && r.IsOnline)
        {
            inTree.Add(root);
            foreach (var e in graph.Neighbours(root)) heap.Push(e, e.Cost);
        }

        while (heap.TryPop(out var edge, out var cost))
        {
            bool aIn = inTree.Contains(edge.A), bIn = inTree.Contains(edge.B);
            if (aIn && bIn) continue;                               // would create a cycle
            var newVertex = aIn ? edge.B : edge.A;
            if (!graph.TryGetVertex(newVertex, out var v) || !v.IsOnline) continue;

            inTree.Add(newVertex);
            mst.Add(edge);
            total += cost;
            foreach (var next in graph.Neighbours(newVertex))
            {
                if (!inTree.Contains(next.Other(newVertex))) heap.Push(next, next.Cost);
            }
        }

        double fullCost = graph.Edges
            .Where(e => graph.TryGetVertex(e.A, out var a) && a.IsOnline && graph.TryGetVertex(e.B, out var b) && b.IsOnline)
            .Sum(e => e.Cost);
        var excluded = graph.Vertices.Where(v => !inTree.Contains(v.Id)).Select(v => v.Id).ToList();
        return new MstResult(mst, Math.Round(total, 2), Math.Round(fullCost, 2), inTree.Count, excluded);
    }

    /// <summary>Route from <paramref name="from"/> to <paramref name="to"/> using only MST edges (BFS on the tree).</summary>
    public static RouteResult RouteOverTree(IReadOnlyList<MeshEdge> treeEdges, string from, string to)
    {
        var adj = new Dictionary<string, List<MeshEdge>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in treeEdges)
        {
            (adj.TryGetValue(e.A, out var la) ? la : adj[e.A] = []).Add(e);
            (adj.TryGetValue(e.B, out var lb) ? lb : adj[e.B] = []).Add(e);
        }

        var parent = new Dictionary<string, (string Prev, MeshEdge Edge)>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from };
        var q = new Queue<string>();
        q.Enqueue(from);
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (string.Equals(cur, to, StringComparison.OrdinalIgnoreCase)) break;
            if (!adj.TryGetValue(cur, out var list)) continue;
            foreach (var e in list)
            {
                var nxt = e.Other(cur);
                if (seen.Add(nxt))
                {
                    parent[nxt] = (cur, e);
                    q.Enqueue(nxt);
                }
            }
        }

        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && !parent.ContainsKey(to))
            return new RouteResult(false, [], 0, 0, "MST", "Node is not connected to the gateway's spanning tree.");

        var path = new List<string> { to };
        double cost = 0;
        var node = to;
        while (!string.Equals(node, from, StringComparison.OrdinalIgnoreCase))
        {
            var (prev, edge) = parent[node];
            cost += edge.Cost;
            path.Add(prev);
            node = prev;
        }
        path.Reverse();
        return new RouteResult(true, path, path.Count - 1, Math.Round(cost, 2), "MST", null);
    }

    /// <summary>Fewest-hop route rebuilt from a BFS parent map.</summary>
    public static RouteResult RouteFromBfs(MeshGraph graph, BfsResult bfs, string target)
    {
        if (!bfs.Parent.ContainsKey(target))
            return new RouteResult(false, [], 0, 0, "BFS", "Node is offline or isolated from the gateway.");

        var path = new List<string>();
        string? node = target;
        double cost = 0;
        while (node is not null)
        {
            path.Add(node);
            var prev = bfs.Parent[node];
            if (prev is not null)
                cost += graph.Neighbours(node).First(e => string.Equals(e.Other(node), prev, StringComparison.OrdinalIgnoreCase)).Cost;
            node = prev;
        }
        path.Reverse(); // gateway → … → target
        return new RouteResult(true, path, path.Count - 1, Math.Round(cost, 2), "BFS", null);
    }

    /// <summary>Dijkstra (min-heap) - cheapest-energy path on the full mesh, for comparison with the MST route.</summary>
    public static RouteResult Dijkstra(MeshGraph graph, string source, string target)
    {
        var dist = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [source] = 0 };
        var prev = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var heap = new MinHeap<string, double>();
        heap.Push(source, 0);

        while (heap.TryPop(out var u, out var d))
        {
            if (d > dist.GetValueOrDefault(u, double.MaxValue)) continue; // stale entry
            if (string.Equals(u, target, StringComparison.OrdinalIgnoreCase)) break;
            foreach (var e in graph.Neighbours(u))
            {
                var v = e.Other(u);
                if (!graph.TryGetVertex(v, out var vx) || !vx.IsOnline) continue;
                double nd = d + e.Cost;
                if (nd < dist.GetValueOrDefault(v, double.MaxValue))
                {
                    dist[v] = nd;
                    prev[v] = u;
                    heap.Push(v, nd);
                }
            }
        }

        if (!dist.ContainsKey(target)) return new RouteResult(false, [], 0, 0, "Dijkstra", "Unreachable.");
        var path = new List<string> { target };
        var n = target;
        while (prev.TryGetValue(n, out var p)) { path.Add(p); n = p; }
        path.Reverse();
        return new RouteResult(true, path, path.Count - 1, Math.Round(dist[target], 2), "Dijkstra", null);
    }
}
