using SmartX.Core.Contracts;

namespace SmartX.Core.Telemetry;

/// <summary>Type-erased view of a device history so the store can hold float, int and bool series together.</summary>
public interface ITelemetrySeries
{
    TelemetryValueKind ValueKind { get; }
    long TotalReceived { get; }
    int Count { get; }
    DateTimeOffset? LastTimestamp { get; }
    TelemetryPointDto[] GetRecent(int max);
    double[] GetRecentValues(int max);
}

/// <summary>
/// Per-device history stored as a "struct of arrays": one ring buffer of raw <typeparamref name="T"/>
/// values and one of timestamps. Values are never boxed, and memory use is fixed by the capacity.
/// </summary>
public sealed class TelemetrySeries<T> : ITelemetrySeries where T : struct, IEquatable<T>
{
    private readonly object _gate = new();
    private readonly RingBuffer<T> _values;
    private readonly RingBuffer<DateTimeOffset> _timestamps;
    private long _total;

    public TelemetrySeries(int capacity)
    {
        _values = new RingBuffer<T>(capacity);
        _timestamps = new RingBuffer<DateTimeOffset>(capacity);
    }

    public TelemetryValueKind ValueKind => TelemetryValueConverter.KindOf<T>();

    public long TotalReceived => Interlocked.Read(ref _total);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _values.Count;
            }
        }
    }

    public DateTimeOffset? LastTimestamp
    {
        get
        {
            lock (_gate)
            {
                if (_timestamps.Count == 0)
                {
                    return null;
                }

                return _timestamps.Newest;
            }
        }
    }

    public void Add(TelemetryPacket<T> packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        lock (_gate)
        {
            _values.Add(packet.Value);
            _timestamps.Add(packet.Timestamp);
        }

        Interlocked.Increment(ref _total);
    }

    public void AddRange(List<TelemetryPacket<T>> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);
        lock (_gate)
        {
            foreach (TelemetryPacket<T> packet in packets)
            {
                _values.Add(packet.Value);
                _timestamps.Add(packet.Timestamp);
            }
        }

        Interlocked.Add(ref _total, packets.Count);
    }

    public T[] GetRecentRaw(int max)
    {
        lock (_gate)
        {
            return _values.ToArray(max);
        }
    }

    public TelemetryPointDto[] GetRecent(int max)
    {
        lock (_gate)
        {
            T[] values = _values.ToArray(max);
            DateTimeOffset[] stamps = _timestamps.ToArray(max);
            var points = new TelemetryPointDto[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                points[i] = new TelemetryPointDto(stamps[i], TelemetryValueConverter.ToDouble(values[i]));
            }

            return points;
        }
    }

    public double[] GetRecentValues(int max)
    {
        lock (_gate)
        {
            T[] values = _values.ToArray(max);
            var result = new double[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                result[i] = TelemetryValueConverter.ToDouble(values[i]);
            }

            return result;
        }
    }
}
