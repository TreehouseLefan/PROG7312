namespace SmartX.Core.Collections;

/// <summary>
/// Indexed binary min-heap over the integers 0..capacity-1 (graph vertex ids), with decrease-key.
/// <para>
/// A position map (item → slot in the heap array) lets Prim's and Dijkstra's algorithms lower a vertex's
/// priority in O(log n) instead of inserting a duplicate. That keeps the heap at most V entries, so both
/// algorithms run in O(E log V) (Sedgewick and Wayne's "eager" variants).
/// </para>
/// Ties are broken by item id, so results are deterministic.
/// </summary>
public sealed class IndexedMinHeap
{
    private readonly int[] _heap;      // slot -> item
    private readonly int[] _position;  // item -> slot, or -1 when absent
    private readonly double[] _priority;
    private int _count;

    public IndexedMinHeap(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _heap = new int[capacity];
        _position = new int[capacity];
        _priority = new double[capacity];
        Array.Fill(_position, -1);
    }

    public int Count => _count;

    public bool IsEmpty => _count == 0;

    /// <summary>Inserts, decrease-keys and extract-mins performed (shown on the dashboard).</summary>
    public long Operations { get; private set; }

    /// <summary>Element swaps performed while sifting.</summary>
    public long Swaps { get; private set; }

    public bool Contains(int item) => (uint)item < (uint)_position.Length && _position[item] >= 0;

    public double PriorityOf(int item) =>
        Contains(item) ? _priority[item] : throw new InvalidOperationException($"Item {item} is not in the heap.");

    public void Insert(int item, double priority)
    {
        if ((uint)item >= (uint)_position.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(item));
        }

        if (_position[item] >= 0)
        {
            throw new InvalidOperationException($"Item {item} is already in the heap.");
        }

        _priority[item] = priority;
        _heap[_count] = item;
        _position[item] = _count;
        _count++;
        Operations++;
        SiftUp(_count - 1);
    }

    public void DecreasePriority(int item, double priority)
    {
        if (!Contains(item))
        {
            throw new InvalidOperationException($"Item {item} is not in the heap.");
        }

        if (priority > _priority[item])
        {
            throw new ArgumentException("The new priority must not be larger than the current one.", nameof(priority));
        }

        _priority[item] = priority;
        Operations++;
        SiftUp(_position[item]);
    }

    /// <summary>Inserts the item, or lowers its priority if it is already queued with a larger one.</summary>
    public void InsertOrDecrease(int item, double priority)
    {
        if (!Contains(item))
        {
            Insert(item, priority);
        }
        else if (priority < _priority[item])
        {
            DecreasePriority(item, priority);
        }
    }

    public int ExtractMin(out double priority)
    {
        if (_count == 0)
        {
            throw new InvalidOperationException("The heap is empty.");
        }

        int min = _heap[0];
        priority = _priority[min];
        _count--;
        if (_count > 0)
        {
            _heap[0] = _heap[_count];
            _position[_heap[0]] = 0;
            SiftDown(0);
        }

        _position[min] = -1;
        Operations++;
        return min;
    }

    private bool Less(int slotA, int slotB)
    {
        int a = _heap[slotA];
        int b = _heap[slotB];
        int c = _priority[a].CompareTo(_priority[b]);
        return c < 0 || (c == 0 && a < b);
    }

    private void Swap(int slotA, int slotB)
    {
        (_heap[slotA], _heap[slotB]) = (_heap[slotB], _heap[slotA]);
        _position[_heap[slotA]] = slotA;
        _position[_heap[slotB]] = slotB;
        Swaps++;
    }

    private void SiftUp(int slot)
    {
        while (slot > 0)
        {
            int parent = (slot - 1) / 2;
            if (!Less(slot, parent))
            {
                break;
            }

            Swap(slot, parent);
            slot = parent;
        }
    }

    private void SiftDown(int slot)
    {
        while (true)
        {
            int left = (2 * slot) + 1;
            if (left >= _count)
            {
                return;
            }

            int smallest = left;
            int right = left + 1;
            if (right < _count && Less(right, left))
            {
                smallest = right;
            }

            if (!Less(smallest, slot))
            {
                return;
            }

            Swap(slot, smallest);
            slot = smallest;
        }
    }
}