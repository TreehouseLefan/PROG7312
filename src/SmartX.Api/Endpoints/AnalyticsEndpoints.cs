using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;
using SmartX.Core.Benchmarks;
using SmartX.Core.Contracts;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Endpoints;

public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder meters = app.MapGroup("/api/meters").WithTags("Power analytics");

        meters.MapGet("/", (MeterAnalyticsService analytics) => Results.Ok(analytics.ListMeters()));

        // GET /api/meters/aggregate?ids=24:0A:C4:0C:00:10&ids=24:0A:C4:0C:00:11&tolerance=100
        meters.MapGet("/aggregate", ([FromQuery] string[]? ids, double? tolerance, MeterAnalyticsService analytics) =>
        {
            MeterAggregationDto? result = analytics.Aggregate(ids, Math.Abs(tolerance ?? 100));
            return result is null
                ? Results.NotFound(new { error = "No matching power meters have reported yet." })
                : Results.Ok(result);
        });

        app.MapGet("/api/zones/{subZoneId}/history-matrix", (
                string subZoneId,
                int? slots,
                DeviceRegistry registry,
                TelemetryStore store) =>
            {
                int columns = Math.Clamp(slots ?? 12, 4, 48);
                List<SensorDevice> devices = registry.All()
                    .Where(d => string.Equals(d.Location.SubZoneId, subZoneId, StringComparison.OrdinalIgnoreCase) &&
                                d.ValueKind != TelemetryValueKind.Bool)
                    .ToList();

                if (devices.Count == 0)
                {
                    return Results.NotFound(new { error = $"Sub-Zone '{subZoneId}' has no numeric devices." });
                }

                var series = new List<double[]>(devices.Count);
                foreach (SensorDevice device in devices)
                {
                    series.Add(store.RecentValues(device.MacAddress, TelemetryStore.SeriesCapacity));
                }

                // Multi-dimensional arrays: [device, time-slot]
                double[,] matrix = TelemetryBatchProcessor.BuildHistoryMatrix(series, columns);
                double[,] deviation = TelemetryBatchProcessor.NormaliseRows(matrix);

                return Results.Ok(new HistoryMatrixDto(
                    subZoneId,
                    columns,
                    devices.Select(d => $"{d.DisplayName} ({d.Unit})").ToArray(),
                    TelemetryBatchProcessor.ToJagged(matrix),
                    TelemetryBatchProcessor.ToJagged(deviation),
                    TelemetryBatchProcessor.ColumnAverages(deviation)));
            })
            .WithTags("Power analytics");

        RouteGroupBuilder diagnostics = app.MapGroup("/api/diagnostics").WithTags("Diagnostics");

        diagnostics.MapGet("/boxing", (int? count) => Results.Ok(BoxingComparison.Run(count ?? 200_000)));

        // Dictionary<MacKey, T> O(1) lookup vs a linear list scan, plus hash-collision counts.
        diagnostics.MapGet("/lookup", (int? devices, int? lookups) =>
            Results.Ok(LookupBenchmark.Run(devices ?? 10_000, lookups ?? 5_000)));

        diagnostics.MapPost("/batch-demo", (int? devices, int? batches, int? readings) =>
            Results.Ok(BatchDemo.Run(devices ?? 50, batches ?? 20, readings ?? 120)));

        return app;
    }
}
