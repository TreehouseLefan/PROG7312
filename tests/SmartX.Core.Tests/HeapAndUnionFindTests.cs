using SmartX.Core.Collections;

namespace SmartX.Core.Tests;

public class HeapAndUnionFindTests
{
    [Fact]
    public void Indexed_heap_extracts_in_priority_order_after_decrease_key()
    {
        var heap = new IndexedMinHeap(5);
        heap.Insert(0, 50);
        heap.Insert(1, 40);
        heap.Insert(2, 30);
        heap.Insert(3, 20);
        heap.DecreasePriority(0, 10); 
        heap.InsertOrDecrease(4, 25);
        heap.InsertOrDecrease(4, 99); // ignored instead of a decrease

        var order = new List<int>();
        while (!heap.IsEmpty)
        {
            order.Add(heap.ExtractMin(out _));
        }

        Assert.Equal(new[] { 0, 3, 4, 2, 1 }, order);
    }

    [Fact]
    public void Binary_heap_heapify_pops_sorted()
    {
        var random = new Random(3);
        int[] values = Enumerable.Range(0, 1_000).Select(_ => random.Next(10_000)).ToArray();
        var heap = new BinaryMinHeap<int>(values);

        var popped = new List<int>();
        while (heap.TryPop(out int value))
        {
            popped.Add(value);
        }

        Assert.Equal(values.Order(), popped);
    }

    [Fact]
    public void Union_find_tracks_components()
    {
        var sets = new DisjointSet(6);

        Assert.True(sets.Union(0, 1));
        Assert.True(sets.Union(2, 3));
        Assert.True(sets.Union(1, 3));
        Assert.False(sets.Union(0, 2)); 

        Assert.True(sets.Connected(0, 3));
        Assert.False(sets.Connected(0, 4));
        Assert.Equal(3, sets.Sets); // {0,1,2,3}, {4}, {5}
    }
}
