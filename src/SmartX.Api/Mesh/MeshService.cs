using System.Diagnostics;
using System.Globalization;
using SmartX.Api.Services;
using SmartX.Core.Contracts;
using SmartX.Core.Devices;
using SmartX.Core.Mesh;

namespace SmartX.Api.Mesh;

/// <summary>
/// Owns the live mesh graph and keeps its routing trees current.
/// <list type="bullet">
/// <item>A node is down when an operator fails it on the Mesh page, or when the Part 2 alert sets
/// (<c>HashSet&lt;MacKey&gt;</c> of disconnected nodes) say it stopped reporting.</item>
/// <item>Whenever the set of down nodes changes, Prim (MST), Kruskal (cross-check), Dijkstra (comparison)
/// and Tarjan's articulation points are recomputed over the live nodes only. That recomputation is the
/// self-healing step: every node whose next hop died gets a new one, and nodes with no path left are
/// reported as isolated.</item>
/// </list>
/// </summary>
public sealed class MeshService
{
    private const int MaxEvents = 25;
    private static readonly TimeSpan RestoreGrace = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<HealEventDto> _events = new();
    private readonly Dictionary<string, DateTimeOffset> _restoredUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeviceRegistry _registry;
    private readonly AlertCenter _alerts;
    private readonly SimulatorControl _simulator;
    private readonly TimelineService _timeline;
    private readonly TimeProvider _time;
    private readonly RadioEnergyModel _model = RadioEnergyModel.Default;

    private MeshGraph? _graph;
    private int _graphDeviceCount = -1;
    private int[] _baselineParent = Array.Empty<int>();
    private Computation? _current;
    private string _signature = string.Empty;
    private string? _pendingAction;
    private long _version;

    public MeshService(
        DeviceRegistry registry,
        AlertCenter alerts,
        SimulatorControl simulator,
        TimelineService timeline,
        TimeProvider time)
    {
        _registry = registry;
        _alerts = alerts;
        _simulator = simulator;
        _timeline = timeline;
        _time = time;
    }

    public long Version => Interlocked.Read(ref _version);

    public RadioEnergyModel Model => _model;

    /// <summary>Recomputes if the graph or the set of down nodes changed. Returns true when it did.</summary>
    public bool Refresh()
    {
        lock (_gate)
        {
            return EnsureCurrent();
        }
    }

    public MeshSnapshotDto Snapshot()
    {
        lock (_gate)
        {
            EnsureCurrent();
            return _current!.Snapshot;
        }
    }

    // ------------------------------------------------------------------
    // Fail / restore (self-healing demo)
    // ------------------------------------------------------------------

    public GatewayOutcome<MeshSnapshotDto> Fail(string idOrMac)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            EnsureCurrent();
            if (!TryResolve(idOrMac, out int v))
            {
                return GatewayOutcome.NotFound<MeshSnapshotDto>($"No mesh node '{idOrMac}'.");
            }

            MeshVertex vertex = _graph!.Vertices[v];
            if (vertex.Kind == MeshNodeKind.Server)
            {
                return GatewayOutcome.Invalid<MeshSnapshotDto>("node", "The central server cannot be failed in this simulation.");
            }

            if (!_failed.Add(vertex.Id))
            {
                return GatewayOutcome.Invalid<MeshSnapshotDto>("node", $"{vertex.Name} is already failed.");
            }

            if (vertex.Mac is not null)
            {
                // A failed ESP32 really stops transmitting, so Parts 1 and 2 see it go offline too.
                _simulator.Disconnect(vertex.Mac, TimeSpan.FromDays(7), now);
            }

            _pendingAction = $"Failed|{vertex.Id}";
            Recompute(now);
            _timeline.AddHistory(HistoryKind.Mesh, $"Node failed: {vertex.Name}", LastEventDetail(), vertex.Mac, now);
            return GatewayOutcome.Ok(_current!.Snapshot);
        }
    }

    public GatewayOutcome<MeshSnapshotDto> Restore(string idOrMac)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            EnsureCurrent();
            if (!TryResolve(idOrMac, out int v))
            {
                return GatewayOutcome.NotFound<MeshSnapshotDto>($"No mesh node '{idOrMac}'.");
            }

            MeshVertex vertex = _graph!.Vertices[v];
            if (!_failed.Remove(vertex.Id))
            {
                return GatewayOutcome.Invalid<MeshSnapshotDto>("node", $"{vertex.Name} was not failed from the Mesh page.");
            }

            if (vertex.Mac is not null)
            {
                _simulator.Reconnect(vertex.Mac);

                // The device needs a few seconds to report again before the alert sets clear it;
                // treat it as up meanwhile so the restore heals the routes immediately.
                _restoredUntil[vertex.Id] = now + RestoreGrace;
            }

            _pendingAction = $"Restored|{vertex.Id}";
            Recompute(now);
            _timeline.AddHistory(HistoryKind.Mesh, $"Node restored: {vertex.Name}", LastEventDetail(), vertex.Mac, now);
            return GatewayOutcome.Ok(_current!.Snapshot);
        }
    }

    public MeshSnapshotDto Reset()
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            EnsureCurrent();
            foreach (string id in _failed)
            {
                if (_graph!.TryGetIndex(id, out int v) && _graph.Vertices[v].Mac is string mac)
                {
                    _simulator.Reconnect(mac);
                    _restoredUntil[id] = now + RestoreGrace;
                }
            }

            _failed.Clear();
            _pendingAction = "Reset|server";
            Recompute(now);
            return _current!.Snapshot;
        }
    }

    // ------------------------------------------------------------------
    // Route tracking
    // ------------------------------------------------------------------

    public RouteReportDto? Route(string idOrMac)
    {
        lock (_gate)
        {
            EnsureCurrent();
            if (!TryResolve(idOrMac, out int v))
            {
                return null;
            }

            Computation c = _current!;
            MeshGraph g = _graph!;
            MeshVertex vertex = g.Vertices[v];
            MeshNodeStatus status = c.Status[v];

            RouteDto mst = BuildRoute("Minimum spanning tree (Prim)", c.Prim.Parent, c.Prim.ParentEdge, c.Root, v);
            RouteDto shortest = BuildRoute("Cheapest individual path (Dijkstra)", c.Dijkstra.Parent, c.Dijkstra.ParentEdge, c.Root, v);

            string? nextHopId = null;
            string? nextHopName = null;
            RouteDto? ifNextHopFails = null;

            if (mst.Reachable && mst.Hops.Length > 0)
            {
                int next = c.Prim.Parent[v];
                MeshVertex nextVertex = g.Vertices[next];
                nextHopId = nextVertex.Id;
                nextHopName = nextVertex.Name;

                if (nextVertex.Kind != MeshNodeKind.Server)
                {
                    // What-if: rerun Prim with the next hop removed, exactly as the mesh would heal.
                    bool IsUpWithoutNext(int x) => x != next && c.Up[x];
                    var alternative = MeshAlgorithms.Prim(g, c.Root, IsUpWithoutNext);
                    ifNextHopFails = BuildRoute($"MST if {nextVertex.Name} fails", alternative.Parent, alternative.ParentEdge, c.Root, v);
                }
            }

            return new RouteReportDto(
                vertex.Id,
                vertex.Name,
                vertex.Mac,
                status,
                mst,
                shortest,
                nextHopId,
                nextHopName,
                ifNextHopFails,
                Explain(vertex, status, mst, shortest, ifNextHopFails));
        }
    }

    // ------------------------------------------------------------------
    // Traversal: which devices hang off a gateway / subnet, which are down, where to hop
    // ------------------------------------------------------------------

    public TraversalReportDto? Traverse(string startIdOrMac, string mode, string? scopeSubZoneId)
    {
        lock (_gate)
        {
            EnsureCurrent();
            if (!TryResolve(startIdOrMac, out int start))
            {
                return null;
            }

            Computation c = _current!;
            MeshGraph g = _graph!;
            MeshVertex startVertex = g.Vertices[start];
            bool depthFirst = string.Equals(mode, "dfs", StringComparison.OrdinalIgnoreCase);

            // From a gateway, stay inside its facility's radio mesh (don't wander back out through the server).
            bool fromServer = startVertex.Kind == MeshNodeKind.Server;
            bool IsTraversable(int x) => c.Up[x] && (fromServer || g.Vertices[x].Kind != MeshNodeKind.Server);

            var stopwatch = Stopwatch.StartNew();
            TraversalResult result = depthFirst
                ? MeshAlgorithms.DepthFirst(g, start, IsTraversable)
                : MeshAlgorithms.BreadthFirst(g, start, IsTraversable);
            stopwatch.Stop();

            bool InScope(int x)
            {
                MeshVertex vx = g.Vertices[x];
                if (vx.Kind == MeshNodeKind.Server)
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(scopeSubZoneId))
                {
                    return string.Equals(vx.SubZoneId, scopeSubZoneId, StringComparison.OrdinalIgnoreCase);
                }

                return fromServer || string.Equals(vx.FacilityId, startVertex.FacilityId, StringComparison.OrdinalIgnoreCase);
            }

            var steps = new List<TraversalStepDto>(result.Order.Length);
            for (int i = 0; i < result.Order.Length; i++)
            {
                int x = result.Order[i];
                MeshVertex vx = g.Vertices[x];
                int parent = result.Parent[x];
                steps.Add(new TraversalStepDto(
                    i,
                    vx.Id,
                    vx.Name,
                    vx.Kind,
                    vx.SubZoneId,
                    result.Depth[x],
                    parent >= 0 ? g.Vertices[parent].Id : null,
                    InScope(x)));
            }

            var down = new List<MeshNodeDto>();
            var unreachable = new List<MeshNodeDto>();
            var advice = new List<HopAdviceDto>();
            int reachableInScope = 0;

            for (int x = 0; x < g.VertexCount; x++)
            {
                if (!InScope(x))
                {
                    continue;
                }

                if (!c.Up[x])
                {
                    down.Add(c.Snapshot.Nodes[x]);
                    continue;
                }

                if (result.Depth[x] >= 0)
                {
                    reachableInScope++;
                }
                else
                {
                    unreachable.Add(c.Snapshot.Nodes[x]);
                }

                int usual = x < _baselineParent.Length ? _baselineParent[x] : -1;
                if (usual >= 0 && !c.Up[usual])
                {
                    int now = c.Prim.Parent[x];
                    MeshVertex vx = g.Vertices[x];
                    MeshVertex usualVertex = g.Vertices[usual];
                    advice.Add(now >= 0
                        ? new HopAdviceDto(vx.Id, vx.Name, usualVertex.Id, usualVertex.Name, g.Vertices[now].Id, g.Vertices[now].Name,
                            $"{usualVertex.Name} is down: {vx.Name} now hops to {g.Vertices[now].Name}.")
                        : new HopAdviceDto(vx.Id, vx.Name, usualVertex.Id, usualVertex.Name, null, null,
                            $"{usualVertex.Name} is down and no other live neighbour leads to the server: {vx.Name} is isolated."));
                }
            }

            string scopeLabel = !string.IsNullOrWhiteSpace(scopeSubZoneId)
                ? $"Sub-Zone {scopeSubZoneId}"
                : fromServer ? "the whole network" : FacilityName(startVertex.FacilityId);
            int maxDepth = result.Order.Length == 0 ? 0 : result.Order.Max(x => result.Depth[x]);
            string summary =
                $"{(depthFirst ? "DFS" : "BFS")} from {startVertex.Name} reached {reachableInScope} live node(s) in {scopeLabel}" +
                $"{(depthFirst ? string.Empty : $", at most {maxDepth} hop(s) away")}. {down.Count} down, {unreachable.Count} alive but unreachable" +
                $"{(advice.Count > 0 ? $", {advice.Count} rerouted around a failed next hop" : string.Empty)}.";

            return new TraversalReportDto(
                depthFirst ? "DFS" : "BFS",
                startVertex.Id,
                startVertex.Name,
                scopeSubZoneId,
                steps.ToArray(),
                reachableInScope,
                down.ToArray(),
                unreachable.ToArray(),
                advice.ToArray(),
                result.EdgesExamined,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds * 1000, 1),
                summary);
        }
    }

    // ------------------------------------------------------------------
    // Computation
    // ------------------------------------------------------------------

    private bool EnsureCurrent()
    {
        IReadOnlyList<SensorDevice> devices = _registry.All();
        bool graphChanged = false;

        if (_graph is null || devices.Count != _graphDeviceCount)
        {
            _graph = MeshGraph.Build(MeshLayout.BuildVertices(devices), _model);
            _graphDeviceCount = devices.Count;
            int root = _graph.IndexOf(MeshLayout.ServerId);
            _baselineParent = MeshAlgorithms.Prim(_graph, root, _ => true).Parent; // everyone's usual next hop
            graphChanged = true;
        }

        bool[] up = ComputeLiveness(_graph);
        string signature = $"{_graph.VertexCount}:{string.Join(',', Enumerable.Range(0, up.Length).Where(i => !up[i]))}";

        if (!graphChanged && signature == _signature && _current is not null)
        {
            return false;
        }

        Recompute(_time.GetUtcNow(), up, signature);
        return true;
    }

    private void Recompute(DateTimeOffset now)
    {
        bool[] up = ComputeLiveness(_graph!);
        string signature = $"{_graph!.VertexCount}:{string.Join(',', Enumerable.Range(0, up.Length).Where(i => !up[i]))}";
        Recompute(now, up, signature);
    }

    private void Recompute(DateTimeOffset now, bool[] up, string signature)
    {
        MeshGraph g = _graph!;
        int root = g.IndexOf(MeshLayout.ServerId);
        bool IsUp(int x) => up[x];

        var stopwatch = Stopwatch.StartNew();
        SpanningTreeResult prim = MeshAlgorithms.Prim(g, root, IsUp);
        double primUs = stopwatch.Elapsed.TotalMilliseconds * 1000;

        stopwatch.Restart();
        SpanningForestResult kruskal = MeshAlgorithms.Kruskal(g, IsUp);
        double kruskalUs = stopwatch.Elapsed.TotalMilliseconds * 1000;

        stopwatch.Restart();
        ShortestPathResult dijkstra = MeshAlgorithms.Dijkstra(g, root, IsUp);
        double dijkstraUs = stopwatch.Elapsed.TotalMilliseconds * 1000;

        bool[] critical = MeshAlgorithms.ArticulationPoints(g, IsUp);

        var status = new MeshNodeStatus[g.VertexCount];
        for (int v = 0; v < g.VertexCount; v++)
        {
            status[v] = !up[v] ? MeshNodeStatus.Down
                : v == root || prim.Parent[v] >= 0 ? MeshNodeStatus.Up
                : MeshNodeStatus.Isolated;
        }

        var mstEdges = new HashSet<int>(prim.TreeEdges);
        var sptEdges = new HashSet<int>(dijkstra.ParentEdge.Where(e => e >= 0));

        var nodes = new MeshNodeDto[g.VertexCount];
        double mstHopsTotal = 0, sptHopsTotal = 0;
        int routedDevices = 0;
        for (int v = 0; v < g.VertexCount; v++)
        {
            MeshVertex vx = g.Vertices[v];
            List<int> mstPath = status[v] == MeshNodeStatus.Up ? MeshAlgorithms.PathToRoot(prim.Parent, root, v) : new List<int>();
            List<int> sptPath = status[v] == MeshNodeStatus.Up ? MeshAlgorithms.PathToRoot(dijkstra.Parent, root, v) : new List<int>();
            double mstCost = PathCost(mstPath, prim.ParentEdge);

            if (mstPath.Count > 1 && vx.Mac is not null)
            {
                mstHopsTotal += mstPath.Count - 1;
                sptHopsTotal += sptPath.Count - 1;
                routedDevices++;
            }

            nodes[v] = new MeshNodeDto(
                vx.Id,
                vx.Mac,
                vx.Name,
                vx.Kind,
                vx.FacilityId,
                vx.SubZoneId,
                vx.X,
                vx.Y,
                status[v],
                _failed.Contains(vx.Id),
                critical[v],
                prim.Parent[v] >= 0 ? g.Vertices[prim.Parent[v]].Id : null,
                mstPath.Count > 0 ? (int?)(mstPath.Count - 1) : null,
                mstPath.Count > 0 ? (double?)Math.Round(mstCost, 1) : null,
                dijkstra.Parent[v] >= 0 ? g.Vertices[dijkstra.Parent[v]].Id : null,
                double.IsFinite(dijkstra.Distance[v]) ? (double?)Math.Round(dijkstra.Distance[v], 1) : null,
                g.Degree(v));
        }

        var edges = new MeshEdgeDto[g.EdgeCount];
        for (int e = 0; e < g.EdgeCount; e++)
        {
            MeshEdge edge = g.Edges[e];
            edges[e] = new MeshEdgeDto(
                e,
                g.Vertices[edge.A].Id,
                g.Vertices[edge.B].Id,
                Math.Round(edge.DistanceMetres, 1),
                Math.Round(edge.Cost, 1),
                edge.Kind,
                mstEdges.Contains(e),
                sptEdges.Contains(e),
                up[edge.A] && up[edge.B]);
        }

        int rootComponent = kruskal.Component[root];
        double kruskalRootCost = kruskal.TreeEdges
            .Where(e => kruskal.Component[g.Edges[e].A] == rootComponent)
            .Sum(e => g.Edges[e].Cost);
        double sptCost = sptEdges.Sum(e => g.Edges[e].Cost);

        var stats = new MeshStatsDto(
            g.VertexCount,
            g.EdgeCount,
            status.Count(s => s == MeshNodeStatus.Up),
            status.Count(s => s == MeshNodeStatus.Down),
            status.Count(s => s == MeshNodeStatus.Isolated),
            prim.TreeEdges.Count,
            Math.Round(prim.TotalCost, 1),
            Math.Round(kruskalRootCost, 1),
            Math.Abs(prim.TotalCost - kruskalRootCost) < 1e-6 * Math.Max(1, prim.TotalCost),
            Math.Round(sptCost, 1),
            Math.Round(mstEdges.Select(e => g.Edges[e].Cost).DefaultIfEmpty(0).Max(), 1),
            Math.Round(sptEdges.Select(e => g.Edges[e].Cost).DefaultIfEmpty(0).Max(), 1),
            routedDevices == 0 ? 0 : Math.Round(mstHopsTotal / routedDevices, 2),
            routedDevices == 0 ? 0 : Math.Round(sptHopsTotal / routedDevices, 2),
            Math.Round(primUs, 1),
            Math.Round(kruskalUs, 1),
            Math.Round(dijkstraUs, 1),
            prim.HeapOperations,
            kruskal.HeapComparisons,
            kruskal.Components,
            Enumerable.Range(0, g.VertexCount).Where(v => critical[v]).Select(v => g.Vertices[v].Id).ToArray());

        Computation? previous = _current;
        double recomputeUs = primUs + kruskalUs + dijkstraUs;
        if (previous is not null && previous.Graph == g)
        {
            LogHealEvent(now, previous, prim, status, stats, recomputeUs);
        }

        _signature = signature;
        _version++;
        var snapshot = new MeshSnapshotDto(
            _version,
            now,
            nodes,
            edges,
            MeshLayout.Areas,
            stats,
            Enumerable.Reverse(_events).ToArray(),
            _model.RangeMetres,
            _model.PacketBits);

        _current = new Computation(g, root, up, status, prim, dijkstra, snapshot);
    }

    private void LogHealEvent(
        DateTimeOffset now,
        Computation previous,
        SpanningTreeResult prim,
        MeshNodeStatus[] status,
        MeshStatsDto stats,
        double recomputeUs)
    {
        MeshGraph g = _graph!;
        int rerouted = 0;
        for (int v = 0; v < g.VertexCount; v++)
        {
            if (previous.Up[v] && status[v] == MeshNodeStatus.Up && previous.Prim.Parent[v] != prim.Parent[v])
            {
                rerouted++;
            }
        }

        var newlyDown = Enumerable.Range(0, g.VertexCount).Where(v => previous.Up[v] && status[v] == MeshNodeStatus.Down).ToList();
        var newlyUp = Enumerable.Range(0, g.VertexCount).Where(v => !previous.Up[v] && status[v] != MeshNodeStatus.Down).ToList();

        string action;
        int subject;
        if (_pendingAction is string pending)
        {
            string[] parts = pending.Split('|');
            action = parts[0];
            subject = g.TryGetIndex(parts[1], out int idx) ? idx : -1;
            _pendingAction = null;
        }
        else if (newlyDown.Count > 0)
        {
            action = "Went offline";
            subject = newlyDown[0];
        }
        else if (newlyUp.Count > 0)
        {
            action = "Came back";
            subject = newlyUp[0];
        }
        else
        {
            return;
        }

        string name = subject >= 0 ? g.Vertices[subject].Name : "network";
        string others = action is "Went offline" && newlyDown.Count > 1 ? $" (+{newlyDown.Count - 1} more)"
            : action is "Came back" && newlyUp.Count > 1 ? $" (+{newlyUp.Count - 1} more)"
            : string.Empty;

        string detail = string.Create(
            CultureInfo.InvariantCulture,
            $"{rerouted} node(s) rerouted; {stats.Isolated} isolated; MST {previous.Snapshot.Stats.MstCostMicrojoules:0} → {stats.MstCostMicrojoules:0} µJ; recomputed in {recomputeUs:0} µs.");

        _events.Enqueue(new HealEventDto(
            now,
            action,
            subject >= 0 ? g.Vertices[subject].Id : string.Empty,
            name + others,
            rerouted,
            stats.Isolated,
            previous.Snapshot.Stats.MstCostMicrojoules,
            stats.MstCostMicrojoules,
            Math.Round(recomputeUs, 1),
            detail));

        while (_events.Count > MaxEvents)
        {
            _events.Dequeue();
        }
    }

    private string LastEventDetail() => _events.Count == 0 ? "Routes recomputed." : _events.Last().Detail;

    private bool[] ComputeLiveness(MeshGraph g)
    {
        DateTimeOffset now = _time.GetUtcNow();
        foreach (string expired in _restoredUntil.Where(p => p.Value <= now).Select(p => p.Key).ToList())
        {
            _restoredUntil.Remove(expired);
        }

        var up = new bool[g.VertexCount];
        for (int v = 0; v < g.VertexCount; v++)
        {
            MeshVertex vx = g.Vertices[v];
            if (vx.Kind == MeshNodeKind.Server)
            {
                up[v] = true;
            }
            else if (_failed.Contains(vx.Id))
            {
                up[v] = false;
            }
            else if (_restoredUntil.ContainsKey(vx.Id))
            {
                up[v] = true;
            }
            else if (vx.Mac is not null && MacKey.TryParse(vx.Mac, out MacKey key))
            {
                up[v] = !_alerts.IsDisconnected(key);
            }
            else
            {
                up[v] = true;
            }
        }

        return up;
    }

    private bool TryResolve(string idOrMac, out int index)
    {
        index = -1;
        if (_graph is null || string.IsNullOrWhiteSpace(idOrMac))
        {
            return false;
        }

        string trimmed = idOrMac.Trim();
        if (_graph.TryGetIndex(trimmed, out index))
        {
            return true;
        }

        return MacKey.TryParse(trimmed, out MacKey key) && _graph.TryGetIndex(key.ToString(), out index);
    }

    private RouteDto BuildRoute(string algorithm, int[] parent, int[] parentEdge, int root, int v)
    {
        MeshGraph g = _graph!;
        List<int> path = MeshAlgorithms.PathToRoot(parent, root, v);
        if (path.Count == 0)
        {
            return new RouteDto(algorithm, false, Array.Empty<RouteHopDto>(), 0, 0, 0, null);
        }

        var hops = new List<RouteHopDto>(path.Count - 1);
        double total = 0, bottleneck = 0;
        for (int i = 0; i < path.Count - 1; i++)
        {
            MeshEdge edge = g.Edges[parentEdge[path[i]]];
            MeshVertex from = g.Vertices[path[i]];
            MeshVertex to = g.Vertices[path[i + 1]];
            hops.Add(new RouteHopDto(from.Id, from.Name, to.Id, to.Name, Math.Round(edge.DistanceMetres, 1), Math.Round(edge.Cost, 1), edge.Kind));
            total += edge.Cost;
            bottleneck = Math.Max(bottleneck, edge.Cost);
        }

        string? exit = path.Count >= 2 ? g.Vertices[path[^2]].Id : null;
        return new RouteDto(algorithm, true, hops.ToArray(), hops.Count, Math.Round(total, 1), Math.Round(bottleneck, 1), exit);
    }

    private double PathCost(List<int> path, int[] parentEdge)
    {
        double total = 0;
        for (int i = 0; i < path.Count - 1; i++)
        {
            total += _graph!.Edges[parentEdge[path[i]]].Cost;
        }

        return total;
    }

    private static string Explain(MeshVertex vertex, MeshNodeStatus status, RouteDto mst, RouteDto shortest, RouteDto? ifFails)
    {
        if (status == MeshNodeStatus.Down)
        {
            return $"{vertex.Name} is down, so it has no route. Its neighbours have already been rerouted around it.";
        }

        if (status == MeshNodeStatus.Isolated)
        {
            return $"{vertex.Name} is alive, but every path to the server runs through failed nodes, so its telemetry cannot reach the server.";
        }

        var ci = CultureInfo.InvariantCulture;
        string text = string.Format(ci,
            "Along the minimum spanning tree, {0} reaches the server in {1} hop(s) for {2:0} µJ per packet, and no single hop costs more than {3:0} µJ. ",
            vertex.Name, mst.HopCount, mst.TotalCostMicrojoules, mst.BottleneckMicrojoules);

        text += Math.Abs(shortest.TotalCostMicrojoules - mst.TotalCostMicrojoules) < 0.05
            ? "That is also its cheapest individual path."
            : string.Format(ci,
                "Its cheapest individual path (Dijkstra) is {0} hop(s) and {1:0} µJ: the MST minimises the energy of the whole backbone and its most expensive link, not each node's own path.",
                shortest.HopCount, shortest.TotalCostMicrojoules);

        if (ifFails is not null)
        {
            text += ifFails.Reachable
                ? string.Format(ci, " If its next hop fails, the mesh heals to a {0}-hop route ({1:0} µJ).", ifFails.HopCount, ifFails.TotalCostMicrojoules)
                : " If its next hop fails, it is cut off: that neighbour is a single point of failure for it.";
        }

        return text;
    }

    private static string FacilityName(string facilityId) =>
        MeshLayout.Areas.FirstOrDefault(a => string.Equals(a.FacilityId, facilityId, StringComparison.OrdinalIgnoreCase))?.Name ?? facilityId;

    private sealed record Computation(
        MeshGraph Graph,
        int Root,
        bool[] Up,
        MeshNodeStatus[] Status,
        SpanningTreeResult Prim,
        ShortestPathResult Dijkstra,
        MeshSnapshotDto Snapshot);
}
