using System.Diagnostics;
using System.Globalization;
using SmartX.Core.Contracts;
using SmartX.Core.Telemetry;

namespace SmartX.Core.Benchmarks;

/// <summary>
/// Evidence for the "no boxing" requirement: stores the same float readings in a generic
/// <see cref="RingBuffer{T}"/> and in a <c>List&lt;object&gt;</c>, and reports the managed
/// bytes each approach allocated on the current thread.
/// </summary>
public static class BoxingComparison
{
    public static BoxingReportDto Run(int count)
    {
        count = Math.Clamp(count, 1_000, 5_000_000);

        // Warm-up so JIT compilation is not included in the measurement.
        Measure(1_000, useBoxing: false);
        Measure(1_000, useBoxing: true);

        var (genericBytes, genericMs, _) = Measure(count, useBoxing: false);
        var (boxedBytes, boxedMs, _) = Measure(count, useBoxing: true);

        double ratio = genericBytes <= 0 ? boxedBytes : (double)boxedBytes / genericBytes;
        string summary = string.Format(
            CultureInfo.InvariantCulture,
            "Storing {0:N0} float readings: the generic buffer allocated {1:N0} bytes, boxing allocated {2:N0} bytes ({3:0.0}x more).",
            count,
            genericBytes,
            boxedBytes,
            ratio);

        return new BoxingReportDto(
            count,
            genericBytes,
            boxedBytes,
            Math.Round(genericMs, 3),
            Math.Round(boxedMs, 3),
            Math.Round(ratio, 2),
            summary);
    }

    private static (long Bytes, double Milliseconds, double Checksum) Measure(int count, bool useBoxing)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        double sum = 0;

        if (useBoxing)
        {
            var boxed = new List<object>(count);
            for (int i = 0; i < count; i++)
            {
                boxed.Add(i * 0.5f); // boxing: a new heap object per reading
            }

            for (int i = 0; i < boxed.Count; i++)
            {
                sum += (float)boxed[i]; // unboxing
            }
        }
        else
        {
            var buffer = new RingBuffer<float>(count);
            for (int i = 0; i < count; i++)
            {
                buffer.Add(i * 0.5f); // stored inline in a float[]
            }

            for (int i = 0; i < buffer.Count; i++)
            {
                sum += TelemetryValueConverter.ToDouble(buffer[i]);
            }
        }

        stopwatch.Stop();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        return (bytes, stopwatch.Elapsed.TotalMilliseconds, sum);
    }
}
