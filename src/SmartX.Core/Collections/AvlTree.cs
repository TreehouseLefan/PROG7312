using System.Diagnostics.CodeAnalysis;

namespace SmartX.Core.Collections;

/// <summary>Count, sum, minimum and maximum of the values in a key range.</summary>
public readonly record struct RangeAggregate(long Count, double Sum, double Min, double Max)
{
    public static RangeAggregate Empty { get; } = new(0, 0, double.PositiveInfinity, double.NegativeInfinity);

    public double Mean => Count == 0 ? double.NaN : Sum / Count;

    public RangeAggregate Combine(RangeAggregate other) =>
        new(Count + other.Count, Sum + other.Sum, Math.Min(Min, other.Min), Math.Max(Max, other.Max));

    public static RangeAggregate Of(double value) => new(1, value, value, value);
}

/// <summary>
/// A self-balancing AVL binary search tree (Adelson-Velsky and Landis, 1962), written from scratch.
/// <para>
/// Every node also stores aggregates of its whole subtree: size, sum, minimum and maximum of a numeric
/// "measure" of the values. That augmentation gives, in O(log n) time:
/// <list type="bullet">
/// <item><see cref="Aggregate"/>: count / sum / min / max / mean of every value in a key range, without visiting
/// the values in between;</item>
/// <item><see cref="CountInRange"/> and rank queries, used for paging;</item>
/// <item><see cref="Page"/>: one page of a range in either order, in O(log n + page size).</item>
/// </list>
/// <see cref="Range"/> lists a whole range in O(log n + k), where k is the number of results.
/// </para>
/// <para>
/// Why AVL and not a plain BST: telemetry arrives in timestamp order. Inserting sorted keys into an
/// unbalanced BST builds a linked list of height n, so every query becomes a full scan. The AVL invariant
/// (child heights differ by at most 1) is restored with at most two rotations per insert, keeping the
/// height below 1.44·log2(n + 2).
/// </para>
/// Not thread-safe; callers synchronise.
/// </summary>
public sealed class AvlTree<TKey, TValue> where TKey : IComparable<TKey>
{
    private readonly Func<TValue, double>? _measure;
    private Node? _root;

    /// <param name="measure">Optional numeric projection of a value used for the subtree aggregates.</param>
    public AvlTree(Func<TValue, double>? measure = null)
    {
        _measure = measure;
    }

    /// <summary>Number of entries.</summary>
    public int Count => SizeOf(_root);

    /// <summary>Height of the tree (0 when empty, 1 for a single node).</summary>
    public int Height => HeightOf(_root);

    /// <summary>Total single and double rotations performed since creation.</summary>
    public long Rotations { get; private set; }

    /// <summary>The largest height an AVL tree with <paramref name="count"/> nodes can have.</summary>
    public static int MaxHeightFor(int count) =>
        count <= 0 ? 0 : (int)Math.Floor((1.4405 * Math.Log2(count + 2)) - 0.3277);

    // ------------------------------------------------------------------
    // Insert / remove
    // ------------------------------------------------------------------

    /// <summary>Adds the entry. Returns false (and replaces the value) if the key already exists.</summary>
    public bool Insert(TKey key, TValue value)
    {
        bool added = false;
        _root = Insert(_root, key, value, ref added);
        return added;
    }

    public bool Remove(TKey key)
    {
        bool removed = false;
        _root = Remove(_root, key, ref removed);
        return removed;
    }

    /// <summary>Removes and returns the entry with the smallest key (used to evict the oldest reading).</summary>
    public bool TryRemoveMin(out TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (_root is null)
        {
            key = default!;
            value = default;
            return false;
        }

        Node min = _root;
        while (min.Left is not null)
        {
            min = min.Left;
        }

        key = min.Key;
        value = min.Value;
        _root = RemoveMin(_root);
        return true;
    }

    public void Clear() => _root = null;

    // ------------------------------------------------------------------
    // Lookups
    // ------------------------------------------------------------------

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        Node? node = _root;
        while (node is not null)
        {
            int c = key.CompareTo(node.Key);
            if (c == 0)
            {
                value = node.Value;
                return true;
            }

            node = c < 0 ? node.Left : node.Right;
        }

        value = default;
        return false;
    }

    public bool TryGetMin(out TKey key)
    {
        Node? node = _root;
        if (node is null)
        {
            key = default!;
            return false;
        }

        while (node.Left is not null)
        {
            node = node.Left;
        }

        key = node.Key;
        return true;
    }

    public bool TryGetMax(out TKey key)
    {
        Node? node = _root;
        if (node is null)
        {
            key = default!;
            return false;
        }

        while (node.Right is not null)
        {
            node = node.Right;
        }

        key = node.Key;
        return true;
    }

    /// <summary>Number of keys strictly smaller than <paramref name="key"/> (its rank). O(log n).</summary>
    public int CountLessThan(TKey key)
    {
        int visited = 0;
        return CountLessThan(key, ref visited);
    }

    /// <summary>Number of keys smaller than or equal to <paramref name="key"/>. O(log n).</summary>
    public int CountLessOrEqual(TKey key)
    {
        int visited = 0;
        return CountLessOrEqual(key, ref visited);
    }

    /// <summary>Number of keys in [from, to]. O(log n).</summary>
    public int CountInRange(TKey from, TKey to) =>
        from.CompareTo(to) > 0 ? 0 : CountLessOrEqual(to) - CountLessThan(from);

    /// <summary>The entry at zero-based position <paramref name="index"/> in key order. O(log n).</summary>
    public KeyValuePair<TKey, TValue> Select(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        Node node = _root!;
        while (true)
        {
            int leftSize = SizeOf(node.Left);
            if (index < leftSize)
            {
                node = node.Left!;
            }
            else if (index == leftSize)
            {
                return new KeyValuePair<TKey, TValue>(node.Key, node.Value);
            }
            else
            {
                index -= leftSize + 1;
                node = node.Right!;
            }
        }
    }

    // ------------------------------------------------------------------
    // Range queries
    // ------------------------------------------------------------------

    /// <summary>
    /// Every entry with from &lt;= key &lt;= to, in key order. Subtrees entirely outside the range are skipped,
    /// so the cost is O(log n + k). <paramref name="nodesVisited"/> reports how many nodes were touched.
    /// </summary>
    public List<KeyValuePair<TKey, TValue>> Range(TKey from, TKey to, out int nodesVisited)
    {
        var result = new List<KeyValuePair<TKey, TValue>>();
        nodesVisited = 0;
        if (from.CompareTo(to) <= 0)
        {
            CollectRange(_root, from, to, result, ref nodesVisited);
        }

        return result;
    }

    /// <summary>
    /// Count, sum, minimum and maximum over [from, to] in O(log n), using the subtree aggregates instead
    /// of visiting the entries in the range.
    /// </summary>
    public RangeAggregate Aggregate(TKey from, TKey to, out int nodesVisited)
    {
        nodesVisited = 0;
        return from.CompareTo(to) > 0 ? RangeAggregate.Empty : AggregateRange(_root, from, to, ref nodesVisited);
    }

    /// <summary>
    /// One page of the entries in [from, to]. Uses the subtree sizes to jump straight to the first entry of
    /// the page and then walks in order, so the cost is O(log n + take) whatever the page number.
    /// </summary>
    public List<KeyValuePair<TKey, TValue>> Page(
        TKey from,
        TKey to,
        int skip,
        int take,
        bool descending,
        out int total,
        out int nodesVisited)
    {
        nodesVisited = 0;
        var result = new List<KeyValuePair<TKey, TValue>>();

        if (from.CompareTo(to) > 0)
        {
            total = 0;
            return result;
        }

        int lo = CountLessThan(from, ref nodesVisited);
        int hi = CountLessOrEqual(to, ref nodesVisited);
        total = Math.Max(0, hi - lo);

        if (total == 0 || skip < 0 || skip >= total || take <= 0)
        {
            return result;
        }

        int count = Math.Min(take, total - skip);
        var stack = new Stack<Node>();

        if (!descending)
        {
            SeekAscending(lo + skip, stack, ref nodesVisited);
            while (result.Count < count && stack.Count > 0)
            {
                Node node = stack.Pop();
                result.Add(new KeyValuePair<TKey, TValue>(node.Key, node.Value));
                for (Node? n = node.Right; n is not null; n = n.Left)
                {
                    stack.Push(n);
                    nodesVisited++;
                }
            }
        }
        else
        {
            SeekDescending(hi - 1 - skip, stack, ref nodesVisited);
            while (result.Count < count && stack.Count > 0)
            {
                Node node = stack.Pop();
                result.Add(new KeyValuePair<TKey, TValue>(node.Key, node.Value));
                for (Node? n = node.Left; n is not null; n = n.Right)
                {
                    stack.Push(n);
                    nodesVisited++;
                }
            }
        }

        return result;
    }

    /// <summary>All entries in key order (iterative in-order traversal).</summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> InOrder()
    {
        var stack = new Stack<Node>();
        Node? node = _root;
        while (node is not null || stack.Count > 0)
        {
            while (node is not null)
            {
                stack.Push(node);
                node = node.Left;
            }

            node = stack.Pop();
            yield return new KeyValuePair<TKey, TValue>(node.Key, node.Value);
            node = node.Right;
        }
    }

    /// <summary>Checks every invariant (ordering, balance, heights, sizes, aggregates). Used by the tests.</summary>
    public bool Validate()
    {
        return Validate(_root, hasLow: false, low: default!, hasHigh: false, high: default!);
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private sealed class Node
    {
        public Node(TKey key, TValue value, double measure)
        {
            Key = key;
            Value = value;
            Measure = measure;
            Height = 1;
            Size = 1;
            Sum = measure;
            Min = measure;
            Max = measure;
        }

        public TKey Key;
        public TValue Value;
        public double Measure;
        public Node? Left;
        public Node? Right;
        public int Height;
        public int Size;
        public double Sum;
        public double Min;
        public double Max;
    }

    private static int HeightOf(Node? node) => node?.Height ?? 0;

    private static int SizeOf(Node? node) => node?.Size ?? 0;

    private double MeasureOf(TValue value) => _measure is null ? 0 : _measure(value);

    private static RangeAggregate Whole(Node? node) =>
        node is null ? RangeAggregate.Empty : new RangeAggregate(node.Size, node.Sum, node.Min, node.Max);

    private static void Update(Node node)
    {
        Node? l = node.Left;
        Node? r = node.Right;
        node.Height = 1 + Math.Max(HeightOf(l), HeightOf(r));
        node.Size = 1 + SizeOf(l) + SizeOf(r);
        node.Sum = node.Measure + (l?.Sum ?? 0) + (r?.Sum ?? 0);
        node.Min = Math.Min(node.Measure, Math.Min(l?.Min ?? double.PositiveInfinity, r?.Min ?? double.PositiveInfinity));
        node.Max = Math.Max(node.Measure, Math.Max(l?.Max ?? double.NegativeInfinity, r?.Max ?? double.NegativeInfinity));
    }

    private Node RotateRight(Node y)
    {
        Node x = y.Left!;
        y.Left = x.Right;
        x.Right = y;
        Update(y);
        Update(x);
        Rotations++;
        return x;
    }

    private Node RotateLeft(Node x)
    {
        Node y = x.Right!;
        x.Right = y.Left;
        y.Left = x;
        Update(x);
        Update(y);
        Rotations++;
        return y;
    }

    /// <summary>Restores |height(left) - height(right)| &lt;= 1 at this node with at most two rotations.</summary>
    private Node Rebalance(Node node)
    {
        Update(node);
        int balance = HeightOf(node.Left) - HeightOf(node.Right);

        if (balance > 1)
        {
            if (HeightOf(node.Left!.Left) < HeightOf(node.Left.Right))
            {
                node.Left = RotateLeft(node.Left); // left-right case
            }

            return RotateRight(node); // left-left case
        }

        if (balance < -1)
        {
            if (HeightOf(node.Right!.Right) < HeightOf(node.Right.Left))
            {
                node.Right = RotateRight(node.Right); // right-left case
            }

            return RotateLeft(node); // right-right case
        }

        return node;
    }

    private Node Insert(Node? node, TKey key, TValue value, ref bool added)
    {
        if (node is null)
        {
            added = true;
            return new Node(key, value, MeasureOf(value));
        }

        int c = key.CompareTo(node.Key);
        if (c < 0)
        {
            node.Left = Insert(node.Left, key, value, ref added);
        }
        else if (c > 0)
        {
            node.Right = Insert(node.Right, key, value, ref added);
        }
        else
        {
            node.Value = value;
            node.Measure = MeasureOf(value);
        }

        return Rebalance(node);
    }

    private Node? Remove(Node? node, TKey key, ref bool removed)
    {
        if (node is null)
        {
            return null;
        }

        int c = key.CompareTo(node.Key);
        if (c < 0)
        {
            node.Left = Remove(node.Left, key, ref removed);
        }
        else if (c > 0)
        {
            node.Right = Remove(node.Right, key, ref removed);
        }
        else
        {
            removed = true;
            if (node.Left is null)
            {
                return node.Right;
            }

            if (node.Right is null)
            {
                return node.Left;
            }

            // Two children: replace this node with its in-order successor.
            Node successor = node.Right;
            while (successor.Left is not null)
            {
                successor = successor.Left;
            }

            successor.Right = RemoveMin(node.Right);
            successor.Left = node.Left;
            return Rebalance(successor);
        }

        return Rebalance(node);
    }

    private Node? RemoveMin(Node node)
    {
        if (node.Left is null)
        {
            return node.Right;
        }

        node.Left = RemoveMin(node.Left);
        return Rebalance(node);
    }

    private int CountLessThan(TKey key, ref int visited)
    {
        int rank = 0;
        Node? node = _root;
        while (node is not null)
        {
            visited++;
            if (key.CompareTo(node.Key) <= 0)
            {
                node = node.Left;
            }
            else
            {
                rank += SizeOf(node.Left) + 1;
                node = node.Right;
            }
        }

        return rank;
    }

    private int CountLessOrEqual(TKey key, ref int visited)
    {
        int rank = 0;
        Node? node = _root;
        while (node is not null)
        {
            visited++;
            if (key.CompareTo(node.Key) < 0)
            {
                node = node.Left;
            }
            else
            {
                rank += SizeOf(node.Left) + 1;
                node = node.Right;
            }
        }

        return rank;
    }

    private static void CollectRange(Node? node, TKey from, TKey to, List<KeyValuePair<TKey, TValue>> into, ref int visited)
    {
        if (node is null)
        {
            return;
        }

        visited++;
        int cFrom = node.Key.CompareTo(from);
        int cTo = node.Key.CompareTo(to);

        if (cFrom > 0)
        {
            CollectRange(node.Left, from, to, into, ref visited); // smaller keys may still be in range
        }

        if (cFrom >= 0 && cTo <= 0)
        {
            into.Add(new KeyValuePair<TKey, TValue>(node.Key, node.Value));
        }

        if (cTo < 0)
        {
            CollectRange(node.Right, from, to, into, ref visited); // larger keys may still be in range
        }
    }

    private static RangeAggregate AggregateRange(Node? node, TKey from, TKey to, ref int visited)
    {
        while (node is not null)
        {
            visited++;
            if (node.Key.CompareTo(from) < 0)
            {
                node = node.Right;
            }
            else if (node.Key.CompareTo(to) > 0)
            {
                node = node.Left;
            }
            else
            {
                // The paths to "from" and "to" split here: everything between them is covered by whole subtrees.
                RangeAggregate left = AggregateAtLeast(node.Left, from, ref visited);
                RangeAggregate right = AggregateAtMost(node.Right, to, ref visited);
                return left.Combine(RangeAggregate.Of(node.Measure)).Combine(right);
            }
        }

        return RangeAggregate.Empty;
    }

    /// <summary>Aggregate of keys &gt;= from in this subtree: one root-to-leaf walk.</summary>
    private static RangeAggregate AggregateAtLeast(Node? node, TKey from, ref int visited)
    {
        RangeAggregate total = RangeAggregate.Empty;
        while (node is not null)
        {
            visited++;
            if (node.Key.CompareTo(from) >= 0)
            {
                total = total.Combine(RangeAggregate.Of(node.Measure)).Combine(Whole(node.Right));
                node = node.Left;
            }
            else
            {
                node = node.Right;
            }
        }

        return total;
    }

    /// <summary>Aggregate of keys &lt;= to in this subtree: one root-to-leaf walk.</summary>
    private static RangeAggregate AggregateAtMost(Node? node, TKey to, ref int visited)
    {
        RangeAggregate total = RangeAggregate.Empty;
        while (node is not null)
        {
            visited++;
            if (node.Key.CompareTo(to) <= 0)
            {
                total = total.Combine(RangeAggregate.Of(node.Measure)).Combine(Whole(node.Left));
                node = node.Right;
            }
            else
            {
                node = node.Left;
            }
        }

        return total;
    }

    /// <summary>Pushes the path to the entry with rank <paramref name="rank"/> for an ascending walk.</summary>
    private void SeekAscending(int rank, Stack<Node> stack, ref int visited)
    {
        Node? node = _root;
        while (node is not null)
        {
            visited++;
            int leftSize = SizeOf(node.Left);
            if (rank < leftSize)
            {
                stack.Push(node); // a larger key we will come back to
                node = node.Left;
            }
            else if (rank == leftSize)
            {
                stack.Push(node);
                return;
            }
            else
            {
                rank -= leftSize + 1;
                node = node.Right;
            }
        }
    }

    /// <summary>Pushes the path to the entry with rank <paramref name="rank"/> for a descending walk.</summary>
    private void SeekDescending(int rank, Stack<Node> stack, ref int visited)
    {
        Node? node = _root;
        while (node is not null)
        {
            visited++;
            int leftSize = SizeOf(node.Left);
            if (rank < leftSize)
            {
                node = node.Left;
            }
            else if (rank == leftSize)
            {
                stack.Push(node);
                return;
            }
            else
            {
                stack.Push(node); // a smaller key we will come back to
                rank -= leftSize + 1;
                node = node.Right;
            }
        }
    }

    private static bool Validate(Node? node, bool hasLow, TKey low, bool hasHigh, TKey high)
    {
        if (node is null)
        {
            return true;
        }

        if ((hasLow && node.Key.CompareTo(low) <= 0) || (hasHigh && node.Key.CompareTo(high) >= 0))
        {
            return false;
        }

        if (!Validate(node.Left, hasLow, low, true, node.Key) || !Validate(node.Right, true, node.Key, hasHigh, high))
        {
            return false;
        }

        int lh = HeightOf(node.Left);
        int rh = HeightOf(node.Right);
        if (Math.Abs(lh - rh) > 1 || node.Height != 1 + Math.Max(lh, rh))
        {
            return false;
        }

        if (node.Size != 1 + SizeOf(node.Left) + SizeOf(node.Right))
        {
            return false;
        }

        double expectedSum = node.Measure + (node.Left?.Sum ?? 0) + (node.Right?.Sum ?? 0);
        return Math.Abs(expectedSum - node.Sum) <= 1e-6 * Math.Max(1, Math.Abs(expectedSum));
    }
}