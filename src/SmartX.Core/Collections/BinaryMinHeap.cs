using System.Diagnostics.CodeAnalysis;

namespace SmartX.Core.Collections;

/// <summary>
/// Array-backed binary min-heap. Building it from a collection uses Floyd's bottom-up heapify, which is
/// O(n) rather than the O(n log n) of n separate inserts. Kruskal's algorithm uses it to take the wireless
/// links cheapest-first without sorting the whole edge list up front.
/// </summary>
public sealed class BinaryMinHeap<T>
{
    private readonly List<T> _items;
    private readonly IComparer<T> _comparer;

    public BinaryMinHeap(IComparer<T>? comparer = null)
    {
        _items = new List<T>();
        _comparer = comparer ?? Comparer<T>.Default;
    }

    /// <summary>Builds the heap in O(n) with Floyd's method.</summary>
    public BinaryMinHeap(IEnumerable<T> items, IComparer<T>? comparer = null)
    {
        _items = new List<T>(items);
        _comparer = comparer ?? Comparer<T>.Default;
        for (int i = (_items.Count / 2) - 1; i >= 0; i--)
        {
            SiftDown(i);
        }
    }

    public int Count => _items.Count;

    /// <summary>Comparisons made so far (shown on the dashboard).</summary>
    public long Comparisons { get; private set; }

    public void Push(T item)
    {
        _items.Add(item);
        SiftUp(_items.Count - 1);
    }

    public T Peek() => _items.Count > 0 ? _items[0] : throw new InvalidOperationException("The heap is empty.");

    public bool TryPop([MaybeNullWhen(false)] out T item)
    {
        if (_items.Count == 0)
        {
            item = default;
            return false;
        }

        item = _items[0];
        int last = _items.Count - 1;
        _items[0] = _items[last];
        _items.RemoveAt(last);
        if (_items.Count > 0)
        {
            SiftDown(0);
        }

        return true;
    }

    public T Pop() => TryPop(out T? item) ? item : throw new InvalidOperationException("The heap is empty.");

    private bool Less(int a, int b)
    {
        Comparisons++;
        return _comparer.Compare(_items[a], _items[b]) < 0;
    }

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            int parent = (i - 1) / 2;
            if (!Less(i, parent))
            {
                return;
            }

            (_items[i], _items[parent]) = (_items[parent], _items[i]);
            i = parent;
        }
    }

    private void SiftDown(int i)
    {
        int n = _items.Count;
        while (true)
        {
            int left = (2 * i) + 1;
            if (left >= n)
            {
                return;
            }

            int smallest = left;
            int right = left + 1;
            if (right < n && Less(right, left))
            {
                smallest = right;
            }

            if (!Less(smallest, i))
            {
                return;
            }

            (_items[i], _items[smallest]) = (_items[smallest], _items[i]);
            i = smallest;
        }
    }
}