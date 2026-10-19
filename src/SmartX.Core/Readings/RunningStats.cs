using System.Globalization;

namespace SmartX.Core.Readings;

/// <summary>
/// Mergeable summary statistics. Operator overloading lets the pulse evaluator write
/// <c>stats += reading</c> and lets zones combine device baselines with <c>zoneStats = a + b</c>.
/// Only count, sum and sum-of-squares are stored, so two summaries can be added exactly.
/// </summary>
public readonly struct RunningStats : IEquatable<RunningStats>
{
    public RunningStats(long count, double sum, double sumOfSquares, double min, double max)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Count = count;
        Sum = sum;
        SumOfSquares = sumOfSquares;
        Min = min;
        Max = max;
    }

    public long Count { get; }
    public double Sum { get; }
    public double SumOfSquares { get; }

    /// <summary>Only meaningful when <see cref="Count"/> is greater than zero.</summary>
    public double Min { get; }

    /// <summary>Only meaningful when <see cref="Count"/> is greater than zero.</summary>
    public double Max { get; }

    public double Mean => Count == 0 ? 0 : Sum / Count;

    /// <summary>Sample variance (n - 1 denominator).</summary>
    public double Variance
    {
        get
        {
            if (Count < 2)
            {
                return 0;
            }

            double variance = (SumOfSquares - (Sum * Sum / Count)) / (Count - 1);
            return variance < 0 ? 0 : variance; // guards against floating-point rounding
        }
    }

    public double StandardDeviation => Math.Sqrt(Variance);

    public double ZScore(double value)
    {
        double sd = StandardDeviation;
        return sd < 1e-9 ? 0 : (value - Mean) / sd;
    }

    /// <summary>Adds one sample.</summary>
    public static RunningStats operator +(RunningStats stats, double sample)
    {
        if (stats.Count == 0)
        {
            return new RunningStats(1, sample, sample * sample, sample, sample);
        }

        return new RunningStats(
            stats.Count + 1,
            stats.Sum + sample,
            stats.SumOfSquares + (sample * sample),
            Math.Min(stats.Min, sample),
            Math.Max(stats.Max, sample));
    }

    /// <summary>Merges two summaries (for example two devices in the same Sub-Zone).</summary>
    public static RunningStats operator +(RunningStats left, RunningStats right)
    {
        if (left.Count == 0)
        {
            return right;
        }

        if (right.Count == 0)
        {
            return left;
        }

        return new RunningStats(
            left.Count + right.Count,
            left.Sum + right.Sum,
            left.SumOfSquares + right.SumOfSquares,
            Math.Min(left.Min, right.Min),
            Math.Max(left.Max, right.Max));
    }

    public static bool operator ==(RunningStats left, RunningStats right) => left.Equals(right);

    public static bool operator !=(RunningStats left, RunningStats right) => !left.Equals(right);

    public static RunningStats From(ReadOnlySpan<double> values)
    {
        RunningStats stats = default;
        foreach (double value in values)
        {
            stats += value;
        }

        return stats;
    }

    public bool Equals(RunningStats other) =>
        Count == other.Count &&
        Sum.Equals(other.Sum) &&
        SumOfSquares.Equals(other.SumOfSquares) &&
        Min.Equals(other.Min) &&
        Max.Equals(other.Max);

    public override bool Equals(object? obj) => obj is RunningStats other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Count, Sum, SumOfSquares, Min, Max);

    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "n={0} mean={1:0.###} sd={2:0.###}",
        Count,
        Mean,
        StandardDeviation);
}
