namespace SmartX.Core.Telemetry;

/// <summary>
/// Fixed-size, array-backed circular buffer. Once full, the oldest item is overwritten,
/// so memory stays constant on resource-constrained gateway devices.
/// Index 0 is always the oldest item.
/// </summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private int _start;
    private int _count;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count => _count;

    public bool IsFull => _count == _items.Length;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _items[(_start + index) % _items.Length];
        }
    }

    public T Newest
    {
        get
        {
            if (_count == 0)
            {
                throw new InvalidOperationException("The buffer is empty.");
            }

            return this[_count - 1];
        }
    }

    public void Add(T item)
    {
        if (_count < _items.Length)
        {
            _items[(_start + _count) % _items.Length] = item;
            _count++;
        }
        else
        {
            _items[_start] = item;
            _start = (_start + 1) % _items.Length;
        }
    }

    /// <summary>Copies the newest items (oldest first) into <paramref name="destination"/>.</summary>
    public int CopyLatest(Span<T> destination)
    {
        int take = Math.Min(destination.Length, _count);
        int offset = _count - take;
        for (int i = 0; i < take; i++)
        {
            destination[i] = this[offset + i];
        }

        return take;
    }

    public T[] ToArray(int max = int.MaxValue)
    {
        int take = Math.Min(max, _count);
        if (take <= 0)
        {
            return Array.Empty<T>();
        }

        var result = new T[take];
        CopyLatest(result);
        return result;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        _count = 0;
    }
}
