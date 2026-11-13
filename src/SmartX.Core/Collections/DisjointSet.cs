namespace SmartX.Core.Collections;

/// <summary>
/// Union-find with union by rank and path halving. Kruskal's algorithm uses it to reject a link that would
/// close a cycle: Find and Union run in near-constant amortised time (inverse Ackermann).
/// </summary>
public sealed class DisjointSet
{
    private readonly int[] _parent;
    private readonly byte[] _rank;

    public DisjointSet(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _parent = new int[count];
        _rank = new byte[count];
        for (int i = 0; i < count; i++)
        {
            _parent[i] = i;
        }

        Sets = count;
    }

    /// <summary>Number of disjoint sets remaining.</summary>
    public int Sets { get; private set; }

    public int Find(int x)
    {
        while (_parent[x] != x)
        {
            _parent[x] = _parent[_parent[x]]; // path halving
            x = _parent[x];
        }

        return x;
    }

    public bool Connected(int a, int b) => Find(a) == Find(b);

    /// <summary>Merges the sets of a and b. Returns false if they were already in the same set.</summary>
    public bool Union(int a, int b)
    {
        int ra = Find(a);
        int rb = Find(b);
        if (ra == rb)
        {
            return false;
        }

        if (_rank[ra] < _rank[rb])
        {
            (ra, rb) = (rb, ra);
        }

        _parent[rb] = ra;
        if (_rank[ra] == _rank[rb])
        {
            _rank[ra]++;
        }

        Sets--;
        return true;
    }
}
