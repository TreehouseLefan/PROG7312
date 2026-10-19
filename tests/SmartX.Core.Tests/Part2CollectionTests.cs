using SmartX.Core.Commands;
using SmartX.Core.Devices;
using SmartX.Core.Streaming;
using SmartX.Core.Timeline;

namespace SmartX.Core.Tests;

public class Part2CollectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    // ---------- Queue<T> + PriorityQueue ----------

    [Fact]
    public void Standard_lane_is_first_in_first_out()
    {
        var queue = new DualLaneQueue<string>();
        queue.TryEnqueueStandard("a", out _);
        queue.TryEnqueueStandard("b", out _);
        queue.TryEnqueueStandard("c", out _);

        var order = new List<string>();
        while (queue.TryDequeue(out Dequeued<string> item))
        {
            order.Add(item.Item);
            Assert.Equal(IngestLane.Standard, item.Lane);
        }

        Assert.Equal(new[] { "a", "b", "c" }, order);
    }

    [Fact]
    public void Critical_item_bypasses_a_full_standard_lane()
    {
        var queue = new DualLaneQueue<string>();
        for (int i = 0; i < 1_000; i++)
        {
            queue.TryEnqueueStandard($"std-{i}", out _);
        }

        queue.EnqueuePriority("critical", AlertSeverity.Critical);

        Assert.True(queue.TryDequeue(out Dequeued<string> first));
        Assert.Equal("critical", first.Item);
        Assert.Equal(IngestLane.Priority, first.Lane);
        Assert.Equal(1_000, queue.StandardCount);
    }

    [Fact]
    public void Priority_lane_orders_by_severity_then_arrival()
    {
        var queue = new DualLaneQueue<string>();
        queue.EnqueuePriority("elevated", AlertSeverity.Elevated);
        queue.EnqueuePriority("critical-1", AlertSeverity.Critical);
        queue.EnqueuePriority("high", AlertSeverity.High);
        queue.EnqueuePriority("critical-2", AlertSeverity.Critical);

        var order = new List<string>();
        while (queue.TryDequeue(out Dequeued<string> item))
        {
            order.Add(item.Item);
        }

        // PriorityQueue alone is not stable; the sequence number keeps equal severities in FIFO order.
        Assert.Equal(new[] { "critical-1", "critical-2", "high", "elevated" }, order);
    }

    [Fact]
    public void Full_standard_lane_sheds_load_but_priority_is_always_accepted()
    {
        var queue = new DualLaneQueue<int>(standardCapacity: 2);

        Assert.True(queue.TryEnqueueStandard(1, out _));
        Assert.True(queue.TryEnqueueStandard(2, out _));
        Assert.False(queue.TryEnqueueStandard(3, out _));
        Assert.Equal(1, queue.Shed);

        queue.EnqueuePriority(99, AlertSeverity.Critical);
        Assert.Equal(1, queue.PriorityCount);
    }

    // ---------- Stack<T> undo / redo ----------

    [Fact]
    public void Undo_returns_the_most_recent_command_first()
    {
        var history = new UndoRedoHistory<string>();
        history.Record("open valve");
        history.Record("start pump");
        history.Record("close all valves");

        Assert.True(history.TryUndo(out string? last));
        Assert.Equal("close all valves", last);
        Assert.True(history.TryUndo(out string? previous));
        Assert.Equal("start pump", previous);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(2, history.RedoCount);
    }

    [Fact]
    public void Redo_restores_and_a_new_command_clears_redo()
    {
        var history = new UndoRedoHistory<string>();
        history.Record("a");
        history.Record("b");
        history.TryUndo(out _);

        Assert.True(history.TryRedo(out string? redone));
        Assert.Equal("b", redone);

        history.TryUndo(out _);
        history.Record("c");
        Assert.Equal(0, history.RedoCount);
        Assert.False(history.TryRedo(out _));
    }

    [Fact]
    public void Undo_stack_is_capped_and_keeps_the_newest()
    {
        var history = new UndoRedoHistory<int>(capacity: 3);
        for (int i = 1; i <= 5; i++)
        {
            history.Record(i);
        }

        Assert.Equal(new[] { 5, 4, 3 }, history.UndoSnapshot());
        Assert.False(history.TryUndo(out _) && history.TryUndo(out _) && history.TryUndo(out _) && history.TryUndo(out _));
    }

    [Fact]
    public void Undo_on_empty_history_is_safe()
    {
        var history = new UndoRedoHistory<string>();
        Assert.False(history.TryUndo(out _));
        Assert.False(history.TryRedo(out _));
    }

    // ---------- MacKey dictionary keys ----------

    [Fact]
    public void Mac_key_parses_every_common_format_to_the_same_key()
    {
        MacKey a = MacKey.Parse("24:0a:c4:12:34:56");
        MacKey b = MacKey.Parse("24-0A-C4-12-34-56");
        MacKey c = MacKey.Parse("240AC4123456");

        Assert.Equal(a, b);
        Assert.Equal(b, c);
        Assert.Equal(a.GetHashCode(), c.GetHashCode());
        Assert.Equal("24:0A:C4:12:34:56", a.ToString());
        Assert.False(MacKey.TryParse("not-a-mac", out _));
    }

    [Fact]
    public void Mac_key_gives_constant_time_dictionary_lookups_without_collisions()
    {
        var map = new Dictionary<MacKey, int>();
        var hashes = new HashSet<int>();
        for (int i = 0; i < 50_000; i++)
        {
            var key = new MacKey(0x240AC4000000UL + (ulong)i);
            map[key] = i;
            hashes.Add(key.GetHashCode());
        }

        Assert.Equal(12_345, map[MacKey.Parse("24:0A:C4:00:30:39")]);
        Assert.Equal(50_000, hashes.Count); // sequential addresses still spread perfectly
    }

    [Fact]
    public void Lookup_benchmark_shows_mac_key_avoids_naive_hash_collisions()
    {
        var report = SmartX.Core.Benchmarks.LookupBenchmark.Run(devices: 8_000, lookups: 500);

        Assert.Equal(0, report.HashCollisions);
        Assert.True(report.NaiveHashCollisions > 0);
    }

    // ---------- SortedDictionary timelines ----------

    [Fact]
    public void Timeline_enumerates_in_time_order_even_when_entries_arrive_out_of_order()
    {
        var store = new TimelineStore<double>();
        store.Add(T0.AddSeconds(20), 3);
        store.Add(T0, 1);
        store.Add(T0.AddSeconds(10), 2); // e.g. a priority packet processed later than a newer one

        List<(DateTimeOffset Timestamp, double Value)> range = store.Range(T0.AddMinutes(-1), T0.AddMinutes(1));

        Assert.Equal(new[] { 1d, 2d, 3d }, range.Select(r => r.Value));
        Assert.Equal(T0.AddSeconds(20), store.Newest);
        Assert.Equal(T0, store.Oldest);
    }

    [Fact]
    public void Timeline_keeps_readings_with_identical_timestamps()
    {
        var store = new TimelineStore<int>();
        store.Add(T0, 1);
        store.Add(T0, 2);

        Assert.Equal(2, store.Count);
        Assert.Equal(new[] { 1, 2 }, store.Range(T0, T0).Select(r => r.Value));
    }

    [Fact]
    public void Timeline_evicts_the_oldest_entry_when_full()
    {
        var store = new TimelineStore<int>(capacity: 3);
        for (int i = 0; i < 5; i++)
        {
            store.Add(T0.AddSeconds(i), i);
        }

        Assert.Equal(3, store.Count);
        Assert.Equal(T0.AddSeconds(2), store.Oldest);
        Assert.Equal(new[] { 4, 3 }, store.Latest(2).Select(r => r.Value));
    }

    [Fact]
    public void Downsampling_keeps_spikes_in_the_bucket_maximum()
    {
        var points = new List<(DateTimeOffset, double)>();
        for (int i = 0; i < 600; i++)
        {
            points.Add((T0.AddSeconds(i), i == 300 ? 99 : 40));
        }

        List<TimelineBucket> buckets = TimelineDownsampler.Downsample(points, T0, T0.AddSeconds(600), 60);

        Assert.True(buckets.Count <= 60);
        Assert.Contains(buckets, b => b.Max == 99);
        Assert.Equal(600, buckets.Sum(b => b.Count));
    }
}
