using System.Diagnostics;
using SmartX.Core.Contracts;
using SmartX.Core.Telemetry;

namespace SmartX.Core.Benchmarks;

/// <summary>
/// Generates irregular historical uploads as jagged arrays (one <c>float[][]</c> per device),
/// flattens them into pre-sized lists, and builds a rectangular device × batch matrix.
/// </summary>
public static class BatchDemo
{
    public static BatchDemoDto Run(int devices, int batchesPerDevice, int maxReadingsPerBatch)
    {
        devices = Math.Clamp(devices, 1, 200);
        batchesPerDevice = Math.Clamp(batchesPerDevice, 1, 100);
        maxReadingsPerBatch = Math.Clamp(maxReadingsPerBatch, 2, 500);

        var random = new Random(42);

        // Jagged array of jagged arrays: [device][batch][reading]; batch lengths differ.
        var uploads = new float[devices][][];
        for (int d = 0; d < devices; d++)
        {
            uploads[d] = new float[batchesPerDevice][];
            for (int b = 0; b < batchesPerDevice; b++)
            {
                int length = random.Next((maxReadingsPerBatch / 2) + 1, maxReadingsPerBatch + 1);
                var readings = new float[length];
                for (int i = 0; i < length; i++)
                {
                    readings[i] = 40f + (float)(random.NextDouble() * 10.0);
                }

                uploads[d][b] = readings;
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        DateTimeOffset start = DateTimeOffset.UtcNow.AddHours(-1);
        int total = 0;
        int capacity = 0;

        for (int d = 0; d < devices; d++)
        {
            List<TelemetryPacket<float>> packets = TelemetryBatchProcessor.FlattenBatches(
                uploads[d],
                $"24:0A:C4:FF:{d / 256:X2}:{d % 256:X2}",
                SensorCategory.Environmental,
                "soil_moisture",
                "%",
                start,
                TimeSpan.FromSeconds(1));

            total += packets.Count;
            capacity += packets.Capacity;
        }

        stopwatch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Rectangular (multi-dimensional) matrix: average of every device's batch.
        var matrix = new double[devices, batchesPerDevice];
        for (int d = 0; d < devices; d++)
        {
            for (int b = 0; b < batchesPerDevice; b++)
            {
                float[] batch = uploads[d][b];
                double sum = 0;
                for (int i = 0; i < batch.Length; i++)
                {
                    sum += batch[i];
                }

                matrix[d, b] = sum / batch.Length;
            }
        }

        BatchSummaryDto[] sample = TelemetryBatchProcessor.Summarise(uploads[0]);

        return new BatchDemoDto(
            devices,
            batchesPerDevice,
            total,
            uploads.Length,
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
            capacity,
            allocated,
            sample.Take(10).ToArray(),
            matrix.GetLength(0),
            matrix.GetLength(1),
            TelemetryBatchProcessor.ColumnAverages(matrix));
    }
}
