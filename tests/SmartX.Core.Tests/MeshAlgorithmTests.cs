using SmartX.Core.Mesh;

namespace SmartX.Core.Tests;

public class MeshAlgorithmTests
{
    
    private static MeshGraph Clrs()
    {
        var graph = new MeshGraph();
        foreach (char c in "abcdefghi")
        {
            graph.AddVertex(new MeshVertex(c.ToString(), c.ToString(), MeshNodeKind.Sensor, 0, 0, "f", "s", null));
        }

        (string A, string B, double W)[] edges =
        {
            ("a", "b", 4), ("a", "h", 8), ("b", "c", 8), ("b", "h", 11), ("c", "d", 7), ("c", "f", 4), ("c", "i", 2),
            ("d", "e", 9), ("d", "f", 14), ("e", "f", 10), ("f", "g", 2), ("g", "h", 1), ("g", "i", 6), ("h", "i", 7)
        };
        foreach ((string a, string b, double w) in edges)
        {
            graph.AddEdge(graph.IndexOf(a), graph.IndexOf(b), w, w, MeshLinkKind.Radio);
        }

        return graph;
    }

    [Fact]
    public void Prim_and_Kruskal_find_the_same_minimum_weight()
    {
        MeshGraph graph = Clrs();

        SpanningTreeResult prim = MeshAlgorithms.Prim(graph, graph.IndexOf("a"), _ => true);
        SpanningForestResult kruskal = MeshAlgorithms.Kruskal(graph, _ => true);

        Assert.Equal(37, prim.TotalCost);
        Assert.Equal(37, kruskal.TotalCost);
        Assert.Equal(8, prim.TreeEdges.Count); 
        Assert.Equal(1, kruskal.Components);
    }

    [Fact]
    public void Dijkstra_matches_hand_computed_distances()
    {
        MeshGraph graph = Clrs();

        ShortestPathResult result = MeshAlgorithms.Dijkstra(graph, graph.IndexOf("a"), _ => true);

        var expected = new Dictionary<string, double>
        {
            ["a"] = 0,
            ["b"] = 4,
            ["c"] = 12,
            ["d"] = 19,
            ["e"] = 21,
            ["f"] = 11,
            ["g"] = 9,
            ["h"] = 8,
            ["i"] = 14
        };
        foreach ((string id, double distance) in expected)
        {
            Assert.Equal(distance, result.Distance[graph.IndexOf(id)]);
        }
    }

    [Fact]
    public void Bfs_depth_is_the_fewest_hops()
    {
        MeshGraph graph = Clrs();

        TraversalResult bfs = MeshAlgorithms.BreadthFirst(graph, graph.IndexOf("a"), _ => true);

        Assert.Equal(9, bfs.Order.Length);
        Assert.Equal(0, bfs.Depth[graph.IndexOf("a")]);
        Assert.Equal(1, bfs.Depth[graph.IndexOf("h")]);
        Assert.Equal(2, bfs.Depth[graph.IndexOf("g")]);
        Assert.Equal(3, bfs.Depth[graph.IndexOf("d")]); // a → b → c → d
        Assert.Equal(4, bfs.Depth[graph.IndexOf("e")]); 
    }

    [Fact]
    public void Dfs_visits_every_reachable_node_once()
    {
        MeshGraph graph = Clrs();

        TraversalResult dfs = MeshAlgorithms.DepthFirst(graph, graph.IndexOf("a"), _ => true);

        Assert.Equal(9, dfs.Order.Length);
        Assert.Equal(9, dfs.Order.Distinct().Count());
        Assert.Equal(graph.IndexOf("a"), dfs.Order[0]);
    }

    [Fact]
    public void Down_nodes_are_skipped_and_the_tree_heals_around_them()
    {
        MeshGraph graph = Clrs();
        int c = graph.IndexOf("c");
        bool Up(int v) => v != c;

        SpanningTreeResult healed = MeshAlgorithms.Prim(graph, graph.IndexOf("a"), Up);
        TraversalResult bfs = MeshAlgorithms.BreadthFirst(graph, graph.IndexOf("a"), Up);

        Assert.Equal(8, healed.Reached);         // everyone except c
        Assert.Equal(-1, healed.Parent[c]);
        Assert.DoesNotContain(c, bfs.Order);
        Assert.True(healed.Parent[graph.IndexOf("d")] >= 0); // d now reached through f
    }

    [Fact]
    public void Articulation_points_are_the_single_points_of_failure()
    {
        // Line a – b – c plus a triangle c – d – e are b and c are cut vertices.
        var graph = new MeshGraph();
        foreach (string id in new[] { "a", "b", "c", "d", "e" })
        {
            graph.AddVertex(new MeshVertex(id, id, MeshNodeKind.Sensor, 0, 0, "f", "s", null));
        }

        void Link(string x, string y) => graph.AddEdge(graph.IndexOf(x), graph.IndexOf(y), 1, 1, MeshLinkKind.Radio);
        Link("a", "b");
        Link("b", "c");
        Link("c", "d");
        Link("d", "e");
        Link("e", "c");

        bool[] critical = MeshAlgorithms.ArticulationPoints(graph, _ => true);

        Assert.Equal(new[] { false, true, true, false, false }, critical);
    }

    [Fact]
    public void Path_to_root_walks_parent_links()
    {
        MeshGraph graph = Clrs();
        int root = graph.IndexOf("a");
        SpanningTreeResult prim = MeshAlgorithms.Prim(graph, root, _ => true);

        List<int> path = MeshAlgorithms.PathToRoot(prim.Parent, root, graph.IndexOf("e"));

        Assert.Equal(graph.IndexOf("e"), path[0]);
        Assert.Equal(root, path[^1]);
    }

    [Fact]
    public void Energy_model_charges_more_for_longer_links()
    {
        RadioEnergyModel model = RadioEnergyModel.Default;

        double near = model.LinkCostMicrojoules(10);
        double far = model.LinkCostMicrojoules(40);

        
        Assert.Equal(112.64, near, 6);
        Assert.Equal(266.24, far, 6);
    }

    [Fact]
    public void Benchmark_costs_agree_at_scale()
    {
        var result = MeshBenchmark.Run(800, seed: 11);

        Assert.True(result.CostsAgree);
        Assert.Equal(800, result.Vertices);
    }
}