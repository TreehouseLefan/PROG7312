using SmartX.Core.Contracts;

namespace SmartX.Core.Telemetry;

/// <summary>
/// Advanced arrays: historical uploads arrive as JAGGED arrays (<c>T[][]</c>, one row per batch,
/// rows of different lengths) and zone history is analysed as a MULTI-DIMENSIONAL array
/// (<c>double[,]</c>, device × time-slot). Results are transferred into pre-sized
/// <see cref="List{T}"/> collections so the list never has to grow and copy.
/// </summary>
public static class TelemetryBatchProcessor
{
    public static int CountReadings<T>(T[][] batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        int total = 0;
        for (int b = 0; b < batches.Length; b++)
        {
            T[]? batch = batches[b];
            if (batch is not null)
            {
                total += batch.Length;
            }
        }

        return total;
    }

    /// <summary>Flattens a jagged array of raw readings into an optimised, pre-sized list of packets.</summary>
    public static List<TelemetryPacket<T>> FlattenBatches<T>(
        T[][] batches,
        string deviceMac,
        SensorCategory category,
        string metric,
        string unit,
        DateTimeOffset firstTimestamp,
        TimeSpan interval) where T : struct, IEquatable<T>
    {
        int total = CountReadings(batches);
        var packets = new List<TelemetryPacket<T>>(total);
        DateTimeOffset timestamp = firstTimestamp;

        for (int b = 0; b < batches.Length; b++)
        {
            T[]? batch = batches[b];
            if (batch is null)
            {
                continue;
            }

            for (int i = 0; i < batch.Length; i++)
            {
                packets.Add(new TelemetryPacket<T>(deviceMac, category, metric, batch[i], timestamp, unit));
                timestamp = timestamp.Add(interval);
            }
        }

        return packets;
    }

    /// <summary>Min / max / mean for every row of a jagged array.</summary>
    public static BatchSummaryDto[] Summarise<T>(T[][] batches) where T : struct
    {
        ArgumentNullException.ThrowIfNull(batches);
        var summaries = new BatchSummaryDto[batches.Length];

        for (int b = 0; b < batches.Length; b++)
        {
            T[]? batch = batches[b];
            if (batch is null || batch.Length == 0)
            {
                summaries[b] = new BatchSummaryDto(b, 0, null, null, null);
                continue;
            }

            double min = double.MaxValue;
            double max = double.MinValue;
            double sum = 0;
            for (int i = 0; i < batch.Length; i++)
            {
                double value = TelemetryValueConverter.ToDouble(batch[i]);
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }

                sum += value;
            }

            summaries[b] = new BatchSummaryDto(b, batch.Length, Math.Round(min, 3), Math.Round(max, 3), Math.Round(sum / batch.Length, 3));
        }

        return summaries;
    }

    /// <summary>
    /// Builds a rectangular device × slot matrix. Each device's history is split evenly into
    /// <paramref name="slots"/> buckets (oldest to newest) and each cell holds the bucket average.
    /// Empty cells are <see cref="double.NaN"/>.
    /// </summary>
    public static double[,] BuildHistoryMatrix(IReadOnlyList<double[]> series, int slots)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);

        var matrix = new double[series.Count, slots];
        var sums = new double[slots];
        var counts = new int[slots];

        for (int row = 0; row < series.Count; row++)
        {
            Array.Clear(sums);
            Array.Clear(counts);
            double[] values = series[row] ?? Array.Empty<double>();

            for (int i = 0; i < values.Length; i++)
            {
                int slot = (int)((long)i * slots / values.Length);
                sums[slot] += values[i];
                counts[slot]++;
            }

            for (int col = 0; col < slots; col++)
            {
                matrix[row, col] = counts[col] > 0 ? sums[col] / counts[col] : double.NaN;
            }
        }

        return matrix;
    }

    /// <summary>Converts each row to z-scores so devices with different units can be compared.</summary>
    public static double[,] NormaliseRows(double[,] matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        int rows = matrix.GetLength(0);
        int cols = matrix.GetLength(1);
        var result = new double[rows, cols];

        for (int r = 0; r < rows; r++)
        {
            double sum = 0;
            double sumSquares = 0;
            int n = 0;
            for (int c = 0; c < cols; c++)
            {
                double v = matrix[r, c];
                if (double.IsFinite(v))
                {
                    sum += v;
                    sumSquares += v * v;
                    n++;
                }
            }

            double mean = n == 0 ? 0 : sum / n;
            double variance = n < 2 ? 0 : Math.Max(0, (sumSquares - (sum * sum / n)) / (n - 1));
            double sd = Math.Sqrt(variance);

            for (int c = 0; c < cols; c++)
            {
                double v = matrix[r, c];
                if (!double.IsFinite(v))
                {
                    result[r, c] = double.NaN;
                }
                else
                {
                    result[r, c] = sd < 1e-9 ? 0 : (v - mean) / sd;
                }
            }
        }

        return result;
    }

    /// <summary>JSON cannot represent multi-dimensional arrays or NaN, so convert to a jagged array of nullable values.</summary>
    public static double?[][] ToJagged(double[,] matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        int rows = matrix.GetLength(0);
        int cols = matrix.GetLength(1);
        var jagged = new double?[rows][];

        for (int r = 0; r < rows; r++)
        {
            var row = new double?[cols];
            for (int c = 0; c < cols; c++)
            {
                double v = matrix[r, c];
                if (double.IsFinite(v))
                {
                    row[c] = Math.Round(v, 3);
                }
                else
                {
                    row[c] = null;
                }
            }

            jagged[r] = row;
        }

        return jagged;
    }

    public static double?[] ColumnAverages(double[,] matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        int rows = matrix.GetLength(0);
        int cols = matrix.GetLength(1);
        var result = new double?[cols];

        for (int c = 0; c < cols; c++)
        {
            double sum = 0;
            int n = 0;
            for (int r = 0; r < rows; r++)
            {
                double v = matrix[r, c];
                if (double.IsFinite(v))
                {
                    sum += v;
                    n++;
                }
            }

            if (n == 0)
            {
                result[c] = null;
            }
            else
            {
                result[c] = Math.Round(sum / n, 3);
            }
        }

        return result;
    }
}
