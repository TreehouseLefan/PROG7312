using System.Collections.Concurrent;
using System.Diagnostics;
using SmartX.Core.Collections;
using SmartX.Core.Contracts;
using SmartX.Core.Devices;
using SmartX.Core.Timeline;

namespace SmartX.Api.Services;

/// <summary>
/// Long-term historical telemetry, one augmented <see cref="AvlTree{TKey,TValue}"/> per device, keyed by
/// (timestamp, sequence). Answers questions such as "all moisture readings for Node A between 14:00 and
/// 16:00".
/// </summary>
public sealed class TelemetryHistoryService
{
    
    public const int CapacityPerDevice = 25_000;

    private readonly ConcurrentDictionary<MacKey, DeviceHistory> _devices = new();

    public void Record(MacKey mac, DateTimeOffset timestamp, double value)
    {
        DeviceHistory history = _devices.GetOrAdd(mac, _ => new DeviceHistory());
        lock (history.Gate)
        {
            history.Tree.Insert(new TimelineKey(timestamp.UtcTicks, ++history.Sequence), value);
            while (history.Tree.Count > CapacityPerDevice)
            {
                history.Tree.TryRemoveMin(out _, out _); // evict the oldest reading in O(log n)
            }
        }
    }

    public int CountFor(MacKey mac)
    {
        if (!_devices.TryGetValue(mac, out DeviceHistory? history))
        {
            return 0;
        }

        lock (history.Gate)
        {
            return history.Tree.Count;
        }
    }

    public bool TryGetOldest(MacKey mac, out DateTimeOffset oldest)
    {
        oldest = default;
        if (!_devices.TryGetValue(mac, out DeviceHistory? history))
        {
            return false;
        }

        lock (history.Gate)
        {
            if (!history.Tree.TryGetMin(out TimelineKey key))
            {
                return false;
            }

            oldest = key.Timestamp;
            return true;
        }
    }

    public HistoryPageDto Query(
        SensorDevice device,
        DateTimeOffset from,
        DateTimeOffset to,
        int page,
        int pageSize,
        bool descending,
        int bucketCount)
    {
        page = Math.Max(0, page);
        pageSize = Math.Clamp(pageSize, 5, 500);
        bucketCount = Math.Clamp(bucketCount, 0, 240);
        if (to < from)
        {
            (from, to) = (to, from);
        }

        DeviceHistory history = _devices.GetOrAdd(device.Key, _ => new DeviceHistory());
        var stopwatch = Stopwatch.StartNew();

        lock (history.Gate)
        {
            AvlTree<TimelineKey, double> tree = history.Tree;
            var fromKey = new TimelineKey(from.UtcTicks, long.MinValue);
            var toKey = new TimelineKey(to.UtcTicks, long.MaxValue);

            List<KeyValuePair<TimelineKey, double>> rows =
                tree.Page(fromKey, toKey, page * pageSize, pageSize, descending, out int total, out int pageVisited);

            RangeAggregate summary = tree.Aggregate(fromKey, toKey, out int summaryVisited);

            var buckets = new List<HistoryBucketDto>(bucketCount);
            int chartVisited = 0;
            if (bucketCount > 0 && summary.Count > 0)
            {
                long width = Math.Max(1, (to.UtcTicks - from.UtcTicks) / bucketCount);
                for (int i = 0; i < bucketCount; i++)
                {
                    long startTicks = from.UtcTicks + (i * width);
                    long endTicks = i == bucketCount - 1 ? to.UtcTicks : startTicks + width - 1;
                    RangeAggregate bucket = tree.Aggregate(
                        new TimelineKey(startTicks, long.MinValue),
                        new TimelineKey(endTicks, long.MaxValue),
                        out int visited);
                    chartVisited += visited;

                    buckets.Add(new HistoryBucketDto(
                        new DateTimeOffset(startTicks, TimeSpan.Zero),
                        new DateTimeOffset(endTicks, TimeSpan.Zero),
                        bucket.Count,
                        bucket.Count == 0 ? (double?)null : Math.Round(bucket.Min, 3),
                        bucket.Count == 0 ? (double?)null : Math.Round(bucket.Max, 3),
                        bucket.Count == 0 ? (double?)null : Math.Round(bucket.Mean, 3)));
                }
            }

            DateTimeOffset? oldest = tree.TryGetMin(out TimelineKey min) ? min.Timestamp : (DateTimeOffset?)null;
            DateTimeOffset? newest = tree.TryGetMax(out TimelineKey max) ? max.Timestamp : (DateTimeOffset?)null;
            stopwatch.Stop();

            return new HistoryPageDto(
                device.MacAddress,
                device.DisplayName,
                device.Metric,
                device.Unit,
                from,
                to,
                total,
                page,
                pageSize,
                descending,
                rows.Select(r => new HistoryItemDto(r.Key.Timestamp, Math.Round(r.Value, 3))).ToArray(),
                new HistorySummaryDto(
                    summary.Count,
                    summary.Count == 0 ? (double?)null : Math.Round(summary.Min, 3),
                    summary.Count == 0 ? (double?)null : Math.Round(summary.Max, 3),
                    summary.Count == 0 ? (double?)null : Math.Round(summary.Mean, 3)),
                buckets.ToArray(),
                new HistoryQueryStatsDto(
                    tree.Count,
                    tree.Height,
                    AvlTree<TimelineKey, double>.MaxHeightFor(tree.Count),
                    tree.Rotations,
                    pageVisited,
                    summaryVisited,
                    chartVisited,
                    tree.Count,
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds * 1000, 1)),
                oldest,
                newest);
        }
    }

    private sealed class DeviceHistory
    {
        public object Gate { get; } = new();

        public AvlTree<TimelineKey, double> Tree { get; } = new(value => value);

        public long Sequence { get; set; }
    }
}