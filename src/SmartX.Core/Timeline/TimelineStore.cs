namespace SmartX.Core.Timeline;

/// <summary>
/// Sort key for timeline entries: the UTC tick count plus a sequence number, so two readings with
/// the same timestamp never collide and keep their arrival order.
/// </summary>
public readonly record struct TimelineKey(long UtcTicks, long Sequence) : IComparable<TimelineKey>
{
    public DateTimeOffset Timestamp => new(UtcTicks, TimeSpan.Zero);

    public int CompareTo(TimelineKey other)
    {
        int byTime = UtcTicks.CompareTo(other.UtcTicks);
        return byTime != 0 ? byTime : Sequence.CompareTo(other.Sequence);
    }
}

/// <summary>
/// Time-ordered log backed by <see cref="SortedDictionary{TKey,TValue}"/> (a binary search tree:
/// O(log n) insert, remove and retrieval). Entries are always enumerated in timestamp order, even when they arrive
/// out of order, for example when a priority-lane packet overtakes older standard-lane packets.
/// That is what lets the dashboard draw a timeline graph without sorting on every request.
/// The store is capped: when full, the oldest entry is removed.
/// </summary>
public sealed class TimelineStore<T>
{
    private readonly object _gate = new();
    private readonly SortedDictionary<TimelineKey, T> _entries = new();
    private long _sequence;
    private TimelineKey? _newest;

    public TimelineStore(int capacity = 2_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public DateTimeOffset? Newest
    {
        get
        {
            lock (_gate)
            {
                return _newest?.Timestamp;
            }
        }
    }

    public DateTimeOffset? Oldest
    {
        get
        {
            lock (_gate)
            {
                foreach (KeyValuePair<TimelineKey, T> pair in _entries)
                {
                    return pair.Key.Timestamp;
                }

                return null;
            }
        }
    }

    public TimelineKey Add(DateTimeOffset timestamp, T value)
    {
        lock (_gate)
        {
            var key = new TimelineKey(timestamp.UtcTicks, ++_sequence);
            _entries.Add(key, value);

            if (_newest is null || key.CompareTo(_newest.Value) > 0)
            {
                _newest = key;
            }

            while (_entries.Count > Capacity)
            {
                // The first key in a SortedDictionary is always the oldest.
                TimelineKey oldest = default;
                foreach (TimelineKey candidate in _entries.Keys)
                {
                    oldest = candidate;
                    break;
                }

                _entries.Remove(oldest);
            }

            return key;
        }
    }

    /// <summary>Entries with from &lt;= timestamp &lt;= to, oldest first.</summary>
    public List<(DateTimeOffset Timestamp, T Value)> Range(DateTimeOffset from, DateTimeOffset to, int max = int.MaxValue)
    {
        var result = new List<(DateTimeOffset, T)>();
        long fromTicks = from.UtcTicks;
        long toTicks = to.UtcTicks;

        lock (_gate)
        {
            foreach (KeyValuePair<TimelineKey, T> pair in _entries)
            {
                if (pair.Key.UtcTicks < fromTicks)
                {
                    continue;
                }

                if (pair.Key.UtcTicks > toTicks || result.Count >= max)
                {
                    break; // sorted order means nothing later can match
                }

                result.Add((pair.Key.Timestamp, pair.Value));
            }
        }

        return result;
    }

    /// <summary>The newest <paramref name="count"/> entries, newest first.</summary>
    public List<(DateTimeOffset Timestamp, T Value)> Latest(int count)
    {
        lock (_gate)
        {
            int skip = Math.Max(0, _entries.Count - count);
            var result = new List<(DateTimeOffset, T)>(Math.Min(count, _entries.Count));
            int index = 0;
            foreach (KeyValuePair<TimelineKey, T> pair in _entries)
            {
                if (index++ >= skip)
                {
                    result.Add((pair.Key.Timestamp, pair.Value));
                }
            }

            result.Reverse();
            return result;
        }
    }
}

/// <summary>One aggregated point of a downsampled numeric timeline.</summary>
public readonly record struct TimelineBucket(DateTimeOffset Timestamp, double Mean, double Min, double Max, int Count);

public static class TimelineDownsampler
{
    /// <summary>
    /// Reduces an ordered series to at most <paramref name="maxPoints"/> buckets of equal duration,
    /// keeping each bucket's mean, minimum and maximum so short spikes are not averaged away.
    /// Runs in a single O(n) pass because the input is already sorted.
    /// </summary>
    public static List<TimelineBucket> Downsample(
        IReadOnlyList<(DateTimeOffset Timestamp, double Value)> points,
        DateTimeOffset from,
        DateTimeOffset to,
        int maxPoints)
    {
        var result = new List<TimelineBucket>();
        if (points.Count == 0 || maxPoints <= 0)
        {
            return result;
        }

        if (points.Count <= maxPoints)
        {
            foreach ((DateTimeOffset timestamp, double value) in points)
            {
                result.Add(new TimelineBucket(timestamp, value, value, value, 1));
            }

            return result;
        }

        long span = Math.Max(1, (to - from).Ticks);
        long bucketTicks = Math.Max(1, span / maxPoints);
        int current = -1;
        double sum = 0, min = 0, max = 0;
        int n = 0;
        long firstTicks = 0;

        foreach ((DateTimeOffset timestamp, double value) in points)
        {
            int bucket = (int)Math.Clamp((timestamp - from).Ticks / bucketTicks, 0, maxPoints - 1);
            if (bucket != current && n > 0)
            {
                result.Add(new TimelineBucket(new DateTimeOffset(firstTicks + ((timestamp.UtcTicks - firstTicks) / 2), TimeSpan.Zero), sum / n, min, max, n));
                n = 0;
            }

            if (n == 0)
            {
                current = bucket;
                sum = 0;
                min = double.MaxValue;
                max = double.MinValue;
                firstTicks = timestamp.UtcTicks;
            }

            sum += value;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
            n++;
        }

        if (n > 0)
        {
            result.Add(new TimelineBucket(new DateTimeOffset(firstTicks, TimeSpan.Zero), sum / n, min, max, n));
        }

        return result;
    }
}
