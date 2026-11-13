using SmartX.Core.Collections;

namespace SmartX.Core.Mesh;

/// <summary>Result of a breadth- or depth-first traversal. Unreached vertices have depth -1.</summary>
public sealed record TraversalResult(int Source, int[] Order, int[] Depth, int[] Parent, int EdgesExamined);

/// <summary>A spanning tree rooted at <see cref="Root"/>: <c>Parent[v]</c> is v's next hop towards the root.</summary>
public sealed record SpanningTreeResult(
    string Algorithm,
    int Root,
    int[] Parent,
    int[] ParentEdge,
    IReadOnlyList<int> TreeEdges,
    double TotalCost,
    int Reached,
    long HeapOperations);

/// <summary>A minimum spanning forest from Kruskal's algorithm; <c>Component[v]</c> is -1 for down vertices.</summary>
public sealed record SpanningForestResult(
    IReadOnlyList<int> TreeEdges,
    double TotalCost,
    int Components,
    int[] Component,
    long HeapComparisons);

/// <summary>Single-source shortest paths: <c>Distance[v]</c> is the cheapest total cost from the source.</summary>
public sealed record ShortestPathResult(int Source, double[] Distance, int[] Parent, int[] ParentEdge, long HeapOperations);

/// <summary>
/// Graph algorithms over a <see cref="MeshGraph"/>. Every method takes <c>isUp</c>, so a failed node is
/// simply skipped: rerunning an algorithm is how the mesh "heals" around it.
/// </summary>
public static class MeshAlgorithms
{
    // ------------------------------------------------------------------
    // Traversal
    // ------------------------------------------------------------------

    /// <summary>
    /// Breadth-first search with a FIFO queue. Vertices come out in order of hop count from the source,
    /// so <c>Depth[v]</c> is the minimum number of hops and <c>Parent[v]</c> the next hop back towards the
    /// source. O(V + E).
    /// </summary>
    public static TraversalResult BreadthFirst(MeshGraph graph, int source, Func<int, bool> isUp)
    {
        ArgumentNullException.ThrowIfNull(graph);
        int n = graph.VertexCount;
        int[] depth = Filled(n, -1);
        int[] parent = Filled(n, -1);
        var order = new List<int>(n);
        int examined = 0;

        if (!isUp(source))
        {
            return new TraversalResult(source, order.ToArray(), depth, parent, 0);
        }

        var queue = new Queue<int>();
        depth[source] = 0;
        queue.Enqueue(source);

        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            order.Add(u);

            foreach (int e in graph.IncidentEdges(u))
            {
                examined++;
                int v = graph.Edges[e].Other(u);
                if (depth[v] >= 0 || !isUp(v))
                {
                    continue;
                }

                depth[v] = depth[u] + 1;
                parent[v] = u;
                queue.Enqueue(v);
            }
        }

        return new TraversalResult(source, order.ToArray(), depth, parent, examined);
    }

    /// <summary>
    /// Iterative depth-first search with an explicit stack (no recursion limit on large meshes). Follows one
    /// chain of links as deep as it goes before backtracking. <c>Depth[v]</c> is the depth in the DFS tree,
    /// not the shortest hop count. O(V + E).
    /// </summary>
    public static TraversalResult DepthFirst(MeshGraph graph, int source, Func<int, bool> isUp)
    {
        ArgumentNullException.ThrowIfNull(graph);
        int n = graph.VertexCount;
        int[] depth = Filled(n, -1);
        int[] parent = Filled(n, -1);
        int[] next = new int[n];
        var order = new List<int>(n);
        int examined = 0;

        if (!isUp(source))
        {
            return new TraversalResult(source, order.ToArray(), depth, parent, 0);
        }

        var stack = new Stack<int>();
        depth[source] = 0;
        order.Add(source);
        stack.Push(source);

        while (stack.Count > 0)
        {
            int u = stack.Peek();
            IReadOnlyList<int> incident = graph.IncidentEdges(u);
            if (next[u] == incident.Count)
            {
                stack.Pop(); // every link of u explored: backtrack
                continue;
            }

            int e = incident[next[u]++];
            examined++;
            int v = graph.Edges[e].Other(u);
            if (depth[v] >= 0 || !isUp(v))
            {
                continue;
            }

            depth[v] = depth[u] + 1;
            parent[v] = u;
            order.Add(v);
            stack.Push(v);
        }

        return new TraversalResult(source, order.ToArray(), depth, parent, examined);
    }

    /// <summary>
    /// Articulation points (cut vertices) via Tarjan's DFS low-link method (Tarjan, 1972): vertices whose
    /// failure would split the live mesh into separate parts, i.e. single points of failure. O(V + E).
    /// </summary>
    public static bool[] ArticulationPoints(MeshGraph graph, Func<int, bool> isUp)
    {
        ArgumentNullException.ThrowIfNull(graph);
        int n = graph.VertexCount;
        int[] discovered = Filled(n, -1);
        int[] low = new int[n];
        int[] parent = Filled(n, -1);
        int[] parentEdge = Filled(n, -1);
        int[] next = new int[n];
        bool[] isCut = new bool[n];
        int time = 0;

        for (int root = 0; root < n; root++)
        {
            if (!isUp(root) || discovered[root] >= 0)
            {
                continue;
            }

            discovered[root] = low[root] = time++;
            int rootChildren = 0;
            var stack = new Stack<int>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                int u = stack.Peek();
                IReadOnlyList<int> incident = graph.IncidentEdges(u);

                if (next[u] < incident.Count)
                {
                    int e = incident[next[u]++];
                    int v = graph.Edges[e].Other(u);
                    if (!isUp(v))
                    {
                        continue;
                    }

                    if (discovered[v] < 0)
                    {
                        parent[v] = u;
                        parentEdge[v] = e;
                        discovered[v] = low[v] = time++;
                        if (u == root)
                        {
                            rootChildren++;
                        }

                        stack.Push(v);
                    }
                    else if (e != parentEdge[u])
                    {
                        low[u] = Math.Min(low[u], discovered[v]); // back edge
                    }
                }
                else
                {
                    stack.Pop();
                    int p = parent[u];
                    if (p >= 0)
                    {
                        low[p] = Math.Min(low[p], low[u]);
                        if (p != root && low[u] >= discovered[p])
                        {
                            isCut[p] = true; // u's subtree has no link that bypasses p
                        }
                    }
                }
            }

            if (rootChildren > 1)
            {
                isCut[root] = true;
            }
        }

        return isCut;
    }

    // ------------------------------------------------------------------
    // Minimum spanning trees
    // ------------------------------------------------------------------

    /// <summary>
    /// Prim's algorithm (Prim, 1957), eager version with an <see cref="IndexedMinHeap"/>: the tree grows from
    /// the server, always adding the cheapest link that reaches a new live node. Only the root's connected
    /// component is spanned, so live nodes left unreached are cut off from the server. O(E log V).
    /// </summary>
    public static SpanningTreeResult Prim(MeshGraph graph, int root, Func<int, bool> isUp)
    {
        ArgumentNullException.ThrowIfNull(graph);
        int n = graph.VertexCount;
        int[] parent = Filled(n, -1);
        int[] parentEdge = Filled(n, -1);
        double[] best = new double[n];
        Array.Fill(best, double.PositiveInfinity);
        bool[] inTree = new bool[n];
        var treeEdges = new List<int>(Math.Max(0, n - 1));
        var heap = new IndexedMinHeap(n);
        double total = 0;
        int reached = 0;

        if (isUp(root))
        {
            best[root] = 0;
            heap.Insert(root, 0);
        }

        while (!heap.IsEmpty)
        {
            int u = heap.ExtractMin(out double weight);
            inTree[u] = true;
            reached++;
            total += weight;
            if (parentEdge[u] >= 0)
            {
                treeEdges.Add(parentEdge[u]);
            }

            foreach (int e in graph.IncidentEdges(u))
            {
                MeshEdge edge = graph.Edges[e];
                int v = edge.Other(u);
                if (inTree[v] || !isUp(v) || edge.Cost >= best[v])
                {
                    continue;
                }

                best[v] = edge.Cost;
                parent[v] = u;
                parentEdge[v] = e;
                heap.InsertOrDecrease(v, edge.Cost);
            }
        }

        return new SpanningTreeResult("Prim", root, parent, parentEdge, treeEdges, total, reached, heap.Operations);
    }

    /// <summary>
    /// Kruskal's algorithm (Kruskal, 1956): take links cheapest-first from a <see cref="BinaryMinHeap{T}"/> and
    /// keep each one that joins two different components of a <see cref="DisjointSet"/>. Produces a minimum
    /// spanning forest over every live node. O(E log E).
    /// </summary>
    public static SpanningForestResult Kruskal(MeshGraph graph, Func<int, bool> isUp)
    {
        ArgumentNullException.ThrowIfNull(graph);
        int n = graph.VertexCount;
        int live = 0;
        for (int v = 0; v < n; v++)
        {
            if (isUp(v))
            {
                live++;
            }
        }

        var candidates = graph.Edges.Where(e => isUp(e.A) && isUp(e.B));
        var heap = new BinaryMinHeap<MeshEdge>(candidates, EdgeComparer.Instance);
        var sets = new DisjointSet(n);
        var treeEdges = new List<int>(Math.Max(0, live - 1));
        double total = 0;

        while (treeEdges.Count < live - 1 && heap.TryPop(out MeshEdge edge))
        {
            if (sets.Union(edge.A, edge.B))
            {
                treeEdges.Add(edge.Index);
                total += edge.Cost;
            }
        }

        int[] component = new int[n];
        var roots = new HashSet<int>();
        for (int v = 0; v < n; v++)
        {
            if (isUp(v))
            {
                component[v] = sets.Find(v);
                roots.Add(component[v]);
            }
            else
            {
                component[v] = -1;
            }
        }

        return new SpanningForestResult(treeEdges, total, roots.Count, component, heap.Comparisons);
    }

    // ------------------------------------------------------------------
    // Shortest paths
    // ------------------------------------------------------------------

    /// <summary>
    /// Dijkstra's algorithm (Dijkstra, 1959) with an <see cref="IndexedMinHeap"/>: the cheapest-energy path
    /// from the source to every live node. Used to compare each node's MST route with its individually
    /// cheapest route. O(E log V).
    /// </summary>
    public static ShortestPathResult Dijkstra(MeshGraph graph, int source, Func<int, bool> isUp)
    {
        ArgumentNullException.ThrowIfNull(graph);
        int n = graph.VertexCount;
        double[] distance = new double[n];
        Array.Fill(distance, double.PositiveInfinity);
        int[] parent = Filled(n, -1);
        int[] parentEdge = Filled(n, -1);
        bool[] done = new bool[n];
        var heap = new IndexedMinHeap(n);

        if (isUp(source))
        {
            distance[source] = 0;
            heap.Insert(source, 0);
        }

        while (!heap.IsEmpty)
        {
            int u = heap.ExtractMin(out double d);
            done[u] = true;

            foreach (int e in graph.IncidentEdges(u))
            {
                MeshEdge edge = graph.Edges[e];
                int v = edge.Other(u);
                if (done[v] || !isUp(v))
                {
                    continue;
                }

                double candidate = d + edge.Cost;
                if (candidate < distance[v])
                {
                    distance[v] = candidate;
                    parent[v] = u;
                    parentEdge[v] = e;
                    heap.InsertOrDecrease(v, candidate);
                }
            }
        }

        return new ShortestPathResult(source, distance, parent, parentEdge, heap.Operations);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Follows parent links from <paramref name="vertex"/> to the root. Returns [vertex, …, root], or an empty
    /// list if the vertex is not in the tree.
    /// </summary>
    public static List<int> PathToRoot(int[] parent, int root, int vertex)
    {
        var path = new List<int>();
        if (vertex != root && parent[vertex] < 0)
        {
            return path;
        }

        int current = vertex;
        int guard = parent.Length + 1;
        while (current >= 0 && guard-- > 0)
        {
            path.Add(current);
            if (current == root)
            {
                return path;
            }

            current = parent[current];
        }

        return new List<int>(); // broken chain: not connected to the root
    }

    private static int[] Filled(int n, int value)
    {
        var array = new int[n];
        Array.Fill(array, value);
        return array;
    }

    private sealed class EdgeComparer : IComparer<MeshEdge>
    {
        public static readonly EdgeComparer Instance = new();

        public int Compare(MeshEdge x, MeshEdge y)
        {
            int c = x.Cost.CompareTo(y.Cost);
            return c != 0 ? c : x.Index.CompareTo(y.Index);
        }
    }
}