using SmartX.Core.Readings;

namespace SmartX.Core.Tests;

public class OperatorOverloadingTests
{
    private static readonly DateTimeOffset T1 = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddSeconds(5);

    [Fact]
    public void Adding_two_meters_aggregates_load()
    {
        var meter1 = new PowerMeterReading("M1", 1200, T1);
        var meter2 = new PowerMeterReading("M2", 800, T2);

        PowerMeterReading meter3 = meter1 + meter2;

        Assert.Equal(2000d, meter3.Watts);
        Assert.Equal("M1+M2", meter3.MeterId);
        Assert.Equal(2, meter3.SourceCount);
        Assert.Equal(T2, meter3.Timestamp);
    }

    [Fact]
    public void Subtracting_meters_gives_the_delta_and_comparisons_work()
    {
        var meter1 = new PowerMeterReading("M1", 1500, T1);
        var meter2 = new PowerMeterReading("M2", 1000, T1);

        Assert.Equal(500d, (meter1 - meter2).Watts);
        Assert.True(meter1 > meter2);
        Assert.True(meter2 < meter1);
        Assert.True(meter1 >= new PowerMeterReading("M9", 1500, T1));
        Assert.False(meter1 == meter2);
        Assert.True(meter1 == new PowerMeterReading("m1", 1500, T2));
        Assert.Equal(1500d, (double)meter1);
    }

    [Fact]
    public void Total_sums_many_readings_with_the_plus_operator()
    {
        var readings = new List<PowerMeterReading>
        {
            new("A", 100, T1),
            new("B", 250, T1),
            new("C", 650, T2)
        };

        PowerMeterReading total = PowerMeterReading.Total(readings, "Sub-Zone C");

        Assert.Equal(1000d, total.Watts);
        Assert.Equal("Sub-Zone C", total.MeterId);
        Assert.Equal(3, total.SourceCount);
    }

    [Fact]
    public void Running_stats_merge_equals_sequential_accumulation()
    {
        double[] left = { 1, 2, 3, 4 };
        double[] right = { 10, 20 };

        RunningStats merged = RunningStats.From(left) + RunningStats.From(right);
        RunningStats sequential = RunningStats.From(left.Concat(right).ToArray());

        Assert.True(merged == sequential);
        Assert.Equal(6.6667, merged.Mean, 3);
        Assert.Equal(1d, merged.Min);
        Assert.Equal(20d, merged.Max);
    }

    [Fact]
    public void Running_stats_standard_deviation_is_the_sample_deviation()
    {
        RunningStats stats = RunningStats.From(new double[] { 2, 4, 4, 4, 5, 5, 7, 9 });

        Assert.Equal(5d, stats.Mean, 6);
        Assert.Equal(2.138, stats.StandardDeviation, 3);
    }
}
