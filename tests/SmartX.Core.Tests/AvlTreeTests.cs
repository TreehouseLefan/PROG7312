using SmartX.Core.Collections;
using SmartX.Core.Timeline;

namespace SmartX.Core.Tests;

public class AvlTreeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    private static AvlTree<int, double> Numbers(int count)
    {
        var tree = new AvlTree<int, double>(v => v);
        for (int i = 0; i < count; i++)
        {
            tree.Insert(i, i); // sorted input way  the worst case forplain BST
        }

        return tree;
    }

    [Fact]
    public void Sorted_inserts_stay_balanced()
    {
        AvlTree<int, double> tree = Numbers(10_000);

        Assert.Equal(10_000, tree.Count);
        Assert.True(tree.Height <= AvlTree<int, double>.MaxHeightFor(tree.Count));
        Assert.True(tree.Height <= 20); // log2(10 000) 13.3 a plain BST would be 10 000 deep
        Assert.True(tree.Rotations > 0);
        Assert.True(tree.Validate());
    }

    [Fact]
    public void Range_returns_keys_in_order_and_inclusive()
    {
        AvlTree<int, double> tree = Numbers(1_000);

        List<KeyValuePair<int, double>> range = tree.Range(100, 199, out int visited);

        Assert.Equal(Enumerable.Range(100, 100), range.Select(p => p.Key));
        Assert.True(visited < 1_000 / 2); 
    }

    [Fact]
    public void Aggregate_matches_a_linear_scan()
    {
        var random = new Random(7);
        var tree = new AvlTree<int, double>(v => v);
        var list = new List<(int Key, double Value)>();
        for (int i = 0; i < 2_000; i++)
        {
            int key = random.Next(0, 100_000);
            double value = Math.Round(random.NextDouble() * 100, 2);
            if (tree.Insert(key, value))
            {
                list.Add((key, value));
            }
        }

        var expected = list.Where(p => p.Key >= 20_000 && p.Key <= 60_000).Select(p => p.Value).ToList();
        RangeAggregate actual = tree.Aggregate(20_000, 60_000, out int visited);

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Sum(), actual.Sum, 6);
        Assert.Equal(expected.Min(), actual.Min);
        Assert.Equal(expected.Max(), actual.Max);
        Assert.True(visited < 100); // O(log n): two root to the leaf paths
    }

    [Fact]
    public void Page_seeks_by_rank_in_both_directions()
    {
        AvlTree<int, double> tree = Numbers(500);

        var ascending = tree.Page(100, 299, skip: 20, take: 5, descending: false, out int total, out _);
        var descending = tree.Page(100, 299, skip: 0, take: 3, descending: true, out _, out _);

        Assert.Equal(200, total);
        Assert.Equal(new[] { 120, 121, 122, 123, 124 }, ascending.Select(p => p.Key));
        Assert.Equal(new[] { 299, 298, 297 }, descending.Select(p => p.Key));
    }

    [Fact]
    public void Rank_and_select_agree()
    {
        AvlTree<int, double> tree = Numbers(300);

        Assert.Equal(150, tree.CountLessThan(150));
        Assert.Equal(151, tree.CountLessOrEqual(150));
        Assert.Equal(50, tree.CountInRange(10, 59));
        Assert.Equal(42, tree.Select(42).Key);
    }

    [Fact]
    public void Remove_and_remove_min_keep_the_tree_valid()
    {
        AvlTree<int, double> tree = Numbers(1_000);

        for (int i = 0; i < 1_000; i += 3)
        {
            Assert.True(tree.Remove(i));
        }

        Assert.False(tree.Remove(0));
        Assert.True(tree.TryRemoveMin(out int min, out _));
        Assert.Equal(1, min);
        Assert.True(tree.Validate());
        Assert.Equal(666, tree.Count);
        Assert.Equal(tree.InOrder().Select(p => p.Key).Order(), tree.InOrder().Select(p => p.Key));
    }

    [Fact]
    public void Timeline_keys_keep_duplicate_timestamps()
    {
        var tree = new AvlTree<TimelineKey, double>(v => v);
        long ticks = T0.UtcTicks;

        tree.Insert(new TimelineKey(ticks, 1), 10);
        tree.Insert(new TimelineKey(ticks, 2), 20); 

        var all = tree.Range(new TimelineKey(ticks, long.MinValue), new TimelineKey(ticks, long.MaxValue), out _);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Two_hour_window_query_returns_only_that_window()
    {
        // One moisture reading per minute for a day.
        var tree = new AvlTree<TimelineKey, double>(v => v);
        for (int minute = 0; minute < 24 * 60; minute++)
        {
            tree.Insert(new TimelineKey(T0.AddMinutes(minute).UtcTicks, minute), minute);
        }

        var from = new TimelineKey(T0.AddHours(14).UtcTicks, long.MinValue);
        var to = new TimelineKey(T0.AddHours(16).UtcTicks, long.MaxValue);
        RangeAggregate window = tree.Aggregate(from, to, out _);

        Assert.Equal(121, window.Count); 
        Assert.Equal(14 * 60, window.Min);
        Assert.Equal(16 * 60, window.Max);
    }
}