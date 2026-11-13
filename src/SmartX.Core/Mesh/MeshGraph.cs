using System.Text.Json.Serialization;

namespace SmartX.Core.Mesh;

[JsonConverter(typeof(JsonStringEnumConverter<MeshNodeKind>))]
public enum MeshNodeKind
{
    Server,
    Gateway,
    Sensor,
    Actuator
}

[JsonConverter(typeof(JsonStringEnumConverter<MeshLinkKind>))]
public enum MeshLinkKind
{
    Radio,
    Backhaul
}

/// <summary>A vertex: the central server, a gateway hub or an ESP32 node, with its position in metres.</summary>
public sealed record MeshVertex(
    string Id,
    string Name,
    MeshNodeKind Kind,
    double X,
    double Y,
    string FacilityId,
    string SubZoneId,
    string? Mac);

/// <summary>An undirected link between vertices A and B, weighted by the energy to send one packet across it.</summary>
public readonly record struct MeshEdge(int Index, int A, int B, double DistanceMetres, double Cost, MeshLinkKind Kind)
{
    public int Other(int vertex) => vertex == A ? B : A;
}

/// <summary>
/// Undirected weighted graph stored as adjacency lists: each vertex keeps the indexes of its incident edges.
/// A mesh is sparse (each node only hears neighbours within radio range), so adjacency lists take O(V + E)
/// memory where a matrix would take O(V²), and a traversal touches each edge at most twice.
/// A <see cref="Dictionary{TKey,TValue}"/> maps vertex ids (and MAC addresses) to array indexes in O(1).
/// </summary>
public sealed class MeshGraph
{
    private readonly List<MeshVertex> _vertices = new();
    private readonly List<MeshEdge> _edges = new();
    private readonly List<List<int>> _incident = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);

    public int VertexCount => _vertices.Count;

    public int EdgeCount => _edges.Count;

    public IReadOnlyList<MeshVertex> Vertices => _vertices;

    public IReadOnlyList<MeshEdge> Edges => _edges;

    public int AddVertex(MeshVertex vertex)
    {
        ArgumentNullException.ThrowIfNull(vertex);
        if (_index.ContainsKey(vertex.Id))
        {
            throw new ArgumentException($"Vertex '{vertex.Id}' already exists.", nameof(vertex));
        }

        int index = _vertices.Count;
        _vertices.Add(vertex);
        _incident.Add(new List<int>());
        _index[vertex.Id] = index;
        if (!string.IsNullOrWhiteSpace(vertex.Mac))
        {
            _index.TryAdd(vertex.Mac, index);
        }

        return index;
    }

    public int AddEdge(int a, int b, double distanceMetres, double cost, MeshLinkKind kind)
    {
        if ((uint)a >= (uint)_vertices.Count || (uint)b >= (uint)_vertices.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(a), "Both endpoints must be existing vertices.");
        }

        if (a == b)
        {
            throw new ArgumentException("A link cannot connect a vertex to itself.", nameof(b));
        }

        if (cost < 0 || !double.IsFinite(cost))
        {
            throw new ArgumentOutOfRangeException(nameof(cost), "Link costs must be finite and non-negative.");
        }

        int index = _edges.Count;
        _edges.Add(new MeshEdge(index, a, b, distanceMetres, cost, kind));
        _incident[a].Add(index);
        _incident[b].Add(index);
        return index;
    }

    /// <summary>Indexes of the edges touching <paramref name="vertex"/>.</summary>
    public IReadOnlyList<int> IncidentEdges(int vertex) => _incident[vertex];

    public int Degree(int vertex) => _incident[vertex].Count;

    /// <summary>Finds a vertex by id or MAC address.</summary>
    public bool TryGetIndex(string idOrMac, out int index) => _index.TryGetValue(idOrMac, out index);

    public int IndexOf(string idOrMac) =>
        _index.TryGetValue(idOrMac, out int index) ? index : throw new KeyNotFoundException($"No vertex '{idOrMac}'.");

    /// <summary>
    /// Builds the physical mesh: every pair of nodes in the same facility within radio range gets a radio link
    /// weighted by <see cref="RadioEnergyModel.LinkCostMicrojoules"/>; every gateway gets a wired backhaul link
    /// to the server. The server never links directly to sensors.
    /// </summary>
    public static MeshGraph Build(IEnumerable<MeshVertex> vertices, RadioEnergyModel model)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(model);

        var graph = new MeshGraph();
        foreach (MeshVertex vertex in vertices)
        {
            graph.AddVertex(vertex);
        }

        for (int i = 0; i < graph.VertexCount; i++)
        {
            MeshVertex a = graph._vertices[i];
            for (int j = i + 1; j < graph.VertexCount; j++)
            {
                MeshVertex b = graph._vertices[j];
                double distance = Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

                bool aServer = a.Kind == MeshNodeKind.Server;
                bool bServer = b.Kind == MeshNodeKind.Server;
                if (aServer || bServer)
                {
                    if ((aServer && b.Kind == MeshNodeKind.Gateway) || (bServer && a.Kind == MeshNodeKind.Gateway))
                    {
                        graph.AddEdge(i, j, distance, model.BackhaulCostMicrojoules, MeshLinkKind.Backhaul);
                    }

                    continue;
                }

                if (string.Equals(a.FacilityId, b.FacilityId, StringComparison.OrdinalIgnoreCase) &&
                    distance <= model.RangeMetres)
                {
                    graph.AddEdge(i, j, distance, model.LinkCostMicrojoules(distance), MeshLinkKind.Radio);
                }
            }
        }

        return graph;
    }
}