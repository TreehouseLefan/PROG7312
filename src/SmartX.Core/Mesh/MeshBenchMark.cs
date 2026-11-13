using System.Diagnostics;
using SmartX.Core.Contracts;

namespace SmartX.Core.Mesh;

/// <summary>
/// Times BFS, Prim, Kruskal and Dijkstra on a random geometric graph (nodes scattered over a square, linked
/// when within radio range), the standard model of a wireless mesh. Shows the algorithms scale well past
/// the 38-node demo mesh, and that Prim and Kruskal agree on the minimum total cost.
/// </summary>
public static class MeshBenchmark
{
    public static MeshBenchmarkDto Run(int nodes, int seed = 2026)
    {
        nodes = Math.Clamp(nodes, 50, 20_000);
        var random = new Random(seed);
        var model = RadioEnergyModel.Default;

        // Keep the average degree roughly constant (~12 neighbours) as the mesh grows.
        double side = Math.Sqrt(nodes * Math.PI * model.RangeMetres * model.RangeMetres / 12.0);

        var vertices = new List<MeshVertex>(nodes + 1)
        {
            new("server", "Central server", MeshNodeKind.Server, side / 2, -10, "bench", "bench", null)
        };

        for (int i = 0; i < nodes; i++)
        {
            MeshNodeKind kind = i % 50 == 0 ? MeshNodeKind.Gateway : MeshNodeKind.Sensor;
            vertices.Add(new MeshVertex($"n{i}", $"Node {i}", kind, random.NextDouble() * side, random.NextDouble() * side, "bench", "bench", null));
        }

        var build = Stopwatch.StartNew();
        MeshGraph graph = BuildGridIndexed(vertices, model);
        build.Stop();

        static bool AllUp(int v) => true;

        var sw = Stopwatch.StartNew();
        TraversalResult bfs = MeshAlgorithms.BreadthFirst(graph, 0, AllUp);
        double bfsMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        SpanningTreeResult prim = MeshAlgorithms.Prim(graph, 0, AllUp);
        double primMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        SpanningForestResult kruskal = MeshAlgorithms.Kruskal(graph, AllUp);
        double kruskalMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        ShortestPathResult dijkstra = MeshAlgorithms.Dijkstra(graph, 0, AllUp);
        double dijkstraMs = sw.Elapsed.TotalMilliseconds;

        int rootComponent = kruskal.Component[0];
        double kruskalRootCost = 0;
        foreach (int e in kruskal.TreeEdges)
        {
            if (kruskal.Component[graph.Edges[e].A] == rootComponent)
            {
                kruskalRootCost += graph.Edges[e].Cost;
            }
        }

        double sptCost = 0;
        foreach (int e in dijkstra.ParentEdge)
        {
            if (e >= 0)
            {
                sptCost += graph.Edges[e].Cost;
            }
        }

        return new MeshBenchmarkDto(
            graph.VertexCount,
            graph.EdgeCount,
            bfs.Order.Length,
            Math.Round(build.Elapsed.TotalMilliseconds, 3),
            Math.Round(bfsMs, 3),
            Math.Round(primMs, 3),
            Math.Round(kruskalMs, 3),
            Math.Round(dijkstraMs, 3),
            Math.Round(prim.TotalCost, 1),
            Math.Round(kruskalRootCost, 1),
            Math.Abs(prim.TotalCost - kruskalRootCost) < 1e-6 * Math.Max(1, prim.TotalCost),
            Math.Round(sptCost, 1),
            prim.HeapOperations,
            kruskal.HeapComparisons);
    }

    /// <summary>
    /// Like <see cref="MeshGraph.Build"/>, but buckets nodes into range-sized grid cells so only neighbouring
    /// cells are compared: O(V) expected instead of O(V²) pair checks for large meshes.
    /// </summary>
    private static MeshGraph BuildGridIndexed(List<MeshVertex> vertices, RadioEnergyModel model)
    {
        var graph = new MeshGraph();
        foreach (MeshVertex vertex in vertices)
        {
            graph.AddVertex(vertex);
        }

        double cell = model.RangeMetres;
        var grid = new Dictionary<(int, int), List<int>>();
        for (int i = 1; i < vertices.Count; i++)
        {
            var key = ((int)Math.Floor(vertices[i].X / cell), (int)Math.Floor(vertices[i].Y / cell));
            if (!grid.TryGetValue(key, out List<int>? bucket))
            {
                bucket = new List<int>();
                grid[key] = bucket;
            }

            bucket.Add(i);
        }

        for (int i = 1; i < vertices.Count; i++)
        {
            MeshVertex a = vertices[i];
            int cx = (int)Math.Floor(a.X / cell);
            int cy = (int)Math.Floor(a.Y / cell);

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy), out List<int>? bucket))
                    {
                        continue;
                    }

                    foreach (int j in bucket)
                    {
                        if (j <= i)
                        {
                            continue;
                        }

                        MeshVertex b = vertices[j];
                        double d = Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
                        if (d <= model.RangeMetres)
                        {
                            graph.AddEdge(i, j, d, model.LinkCostMicrojoules(d), MeshLinkKind.Radio);
                        }
                    }
                }
            }

            if (a.Kind == MeshNodeKind.Gateway)
            {
                graph.AddEdge(0, i, 0, model.BackhaulCostMicrojoules, MeshLinkKind.Backhaul);
            }
        }

        return graph;
    }
}