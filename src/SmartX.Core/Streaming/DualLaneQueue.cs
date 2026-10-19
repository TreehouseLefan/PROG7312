using System.Diagnostics;
using System.Text.Json.Serialization;

namespace SmartX.Core.Streaming;

/// <summary>Which lane an item travelled through.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IngestLane>))]
public enum IngestLane
{
    Standard,
    Priority
}

/// <summary>Severity of a priority item. Lower numbers are served first.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertSeverity>))]
public enum AlertSeverity
{
    Critical = 0,
    High = 1,
    Elevated = 2
}

/// <summary>An item taken from the queue, with the time it waited.</summary>
public readonly record struct Dequeued<T>(T Item, IngestLane Lane, AlertSeverity? Severity, double WaitMilliseconds, long Sequence);

/// <summary>
/// Two-lane ingestion buffer.
/// <list type="bullet">
/// <item><b>Standard lane</b>: a <see cref="Queue{T}"/> giving strict First-In-First-Out order.</item>
/// <item><b>Priority lane</b>: a <see cref="PriorityQueue{TElement,TPriority}"/> (an array-backed quaternary
/// min-heap) ordered by severity. Every dequeue serves the priority lane first, so a critical alert
/// never waits behind standard traffic.</item>
/// </list>
/// .NET's <c>PriorityQueue</c> is not stable: two items with the same priority can come out in either
/// order. Pairing the severity with a monotonically increasing sequence number restores FIFO order
/// within each severity. The standard lane is bounded (back-pressure), the priority lane is not, so
/// critical traffic is never shed.
/// </summary>
public sealed class DualLaneQueue<T>
{
    private readonly object _gate = new();
    private readonly Queue<Entry> _standard = new();
    private readonly PriorityQueue<Entry, (int Severity, long Sequence)> _priority = new(PriorityComparer.Instance);
    private readonly SemaphoreSlim _signal = new(0);
    private long _sequence;
    private long _shed;

    public DualLaneQueue(int standardCapacity = 50_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(standardCapacity);
        StandardCapacity = standardCapacity;
    }

    public int StandardCapacity { get; }

    public int StandardCount
    {
        get
        {
            lock (_gate)
            {
                return _standard.Count;
            }
        }
    }

    public int PriorityCount
    {
        get
        {
            lock (_gate)
            {
                return _priority.Count;
            }
        }
    }

    /// <summary>Standard items refused because the lane was full.</summary>
    public long Shed => Interlocked.Read(ref _shed);

    /// <summary>Adds to the FIFO lane. Returns false (load shedding) if the lane is full.</summary>
    public bool TryEnqueueStandard(T item, out long sequence)
    {
        lock (_gate)
        {
            if (_standard.Count >= StandardCapacity)
            {
                sequence = -1;
                Interlocked.Increment(ref _shed);
                return false;
            }

            sequence = ++_sequence;
            _standard.Enqueue(new Entry(item, IngestLane.Standard, null, Stopwatch.GetTimestamp(), sequence));
        }

        _signal.Release();
        return true;
    }

    /// <summary>Adds to the priority lane. Always succeeds.</summary>
    public long EnqueuePriority(T item, AlertSeverity severity)
    {
        long sequence;
        lock (_gate)
        {
            sequence = ++_sequence;
            _priority.Enqueue(
                new Entry(item, IngestLane.Priority, severity, Stopwatch.GetTimestamp(), sequence),
                ((int)severity, sequence));
        }

        _signal.Release();
        return sequence;
    }

    /// <summary>Takes the next item: priority lane first, then the standard lane in FIFO order.</summary>
    public bool TryDequeue(out Dequeued<T> result)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_priority.TryDequeue(out entry, out _) && !_standard.TryDequeue(out entry))
            {
                result = default;
                return false;
            }
        }

        double waited = Stopwatch.GetElapsedTime(entry.EnqueuedAt).TotalMilliseconds;
        result = new Dequeued<T>(entry.Item, entry.Lane, entry.Severity, waited, entry.Sequence);
        return true;
    }

    /// <summary>Waits until at least one item may be available.</summary>
    public Task WaitAsync(CancellationToken cancellationToken) => _signal.WaitAsync(cancellationToken);

    /// <summary>Waits with a timeout; returns false if nothing arrived in time.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);

    private readonly record struct Entry(T Item, IngestLane Lane, AlertSeverity? Severity, long EnqueuedAt, long Sequence);

    private sealed class PriorityComparer : IComparer<(int Severity, long Sequence)>
    {
        public static readonly PriorityComparer Instance = new();

        public int Compare((int Severity, long Sequence) x, (int Severity, long Sequence) y)
        {
            int bySeverity = x.Severity.CompareTo(y.Severity);
            return bySeverity != 0 ? bySeverity : x.Sequence.CompareTo(y.Sequence);
        }
    }
}
