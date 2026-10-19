using System.Diagnostics;
using SmartX.Api.Services;
using SmartX.Core.Contracts;
using SmartX.Core.Pulse;
using SmartX.Core.Telemetry;
using SmartX.Core.Timeline;

namespace SmartX.Api.Endpoints;

public static class StreamEndpoints
{
    public static IEndpointRouteBuilder MapStreamEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/stream").WithTags("Command Stream");

        // Search / filter devices by text, category and live alert state.
        group.MapGet("/devices", (
            string? query,
            SensorCategory? category,
            string? state,
            bool? alertsOnly,
            bool? record,
            DeviceRegistry registry,
            PulseMonitor pulse,
            AlertCenter alerts,
            CommandService commands,
            ActionEngine engine,
            TimeProvider time) =>
        {
            var stopwatch = Stopwatch.StartNew();
            IReadOnlyList<SensorDevice> matches = registry.Search(query, category, out string strategy);

            PulseState? wantedState = null;
            if (!string.IsNullOrWhiteSpace(state) && !string.Equals(state, "Any", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse(state, ignoreCase: true, out PulseState parsed))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["state"] = new[] { "Use Healthy, Spike, Fading, Offline or Any." }
                    });
                }

                wantedState = parsed;
            }

            var results = new List<DeviceSearchResultDto>();
            foreach (SensorDevice device in matches)
            {
                pulse.TryGetTile(device.MacAddress, out PulseTileDto? tile);
                PulseState tileState = tile?.State ?? PulseState.Unknown;
                AlertType[] active = alerts.ActiveFor(device.Key);

                if (wantedState is PulseState want && tileState != want)
                {
                    continue;
                }

                if (alertsOnly == true && active.Length == 0)
                {
                    continue;
                }

                double score = active.Length * 10 + (tileState is PulseState.Offline or PulseState.Spike ? 5 : 0);
                results.Add(new DeviceSearchResultDto(
                    device.ToDto(0),
                    tileState,
                    tile?.LastValue ?? 0,
                    active,
                    commands.DesiredState(device.Key),
                    score));
            }

            stopwatch.Stop();

            if (record == true && !string.IsNullOrWhiteSpace(query))
            {
                engine.ObserveSearch(query, time.GetUtcNow());
            }

            DeviceSearchResultDto[] ordered = results
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Device.Location.SubZoneId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Device.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToArray();

            return Results.Ok(new DeviceSearchResponseDto(
                query,
                results.Count,
                ordered.Length,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds * 1000, 1),
                strategy,
                ordered));
        });

        // Opening a device is a signal the engine learns from ("Inspect").
        group.MapPost("/devices/{mac}/inspect", (string mac, DeviceRegistry registry, ActionEngine engine, TimeProvider time) =>
        {
            if (!registry.TryGet(mac, out SensorDevice? device))
            {
                return Results.NotFound(new { error = $"Device {mac} is not registered." });
            }

            engine.ObserveInspect(device, time.GetUtcNow());
            return Results.NoContent();
        });

        group.MapGet("/queue", (IngestPipeline pipeline) => Results.Ok(pipeline.Stats()));

        group.MapPost("/queue/pause", (IngestPipeline pipeline) =>
        {
            pipeline.Pause();
            return Results.Ok(pipeline.Stats());
        });

        group.MapPost("/queue/resume", (IngestPipeline pipeline) =>
        {
            pipeline.Resume();
            return Results.Ok(pipeline.Stats());
        });

        group.MapGet("/recent", (int? max, IngestPipeline pipeline) => Results.Ok(pipeline.Recent(Math.Clamp(max ?? 50, 1, 150))));

        // Proof that critical packets bypass the FIFO lane.
        group.MapPost("/burst", async (int? count, DeviceRegistry registry, IngestPipeline pipeline, TimeProvider time, CancellationToken cancellationToken) =>
        {
            List<SensorDevice> numeric = registry.All().Where(d => d.ValueKind == TelemetryValueKind.Float).ToList();
            SensorDevice? critical = numeric.FirstOrDefault(d => d.Metric == "soil_moisture");
            if (numeric.Count == 0 || critical is null)
            {
                return Results.Problem("No float sensors are registered to run the burst test.", statusCode: StatusCodes.Status409Conflict);
            }

            DateTimeOffset now = time.GetUtcNow();
            try
            {
                BurstReportDto report = await pipeline.RunBurstAsync(
                    count ?? 5_000,
                    numeric,
                    critical,
                    device => new TelemetryPacket<float>(device.MacAddress, device.Category, device.Metric, 40f, now, device.Unit),
                    new TelemetryPacket<float>(critical.MacAddress, critical.Category, critical.Metric, 12f, now, critical.Unit),
                    cancellationToken);
                return Results.Ok(report);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (TimeoutException)
            {
                return Results.Problem("The burst did not finish within 30 seconds.", statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });

        // Timeline graph data straight from the device's SortedDictionary.
        app.MapGet("/api/devices/{mac}/timeline", (
                string mac,
                int? minutes,
                int? maxPoints,
                DeviceRegistry registry,
                TimelineService timeline,
                AlertCenter alerts,
                TimeProvider time) =>
            {
                if (!registry.TryGet(mac, out SensorDevice? device))
                {
                    return Results.NotFound(new { error = $"Device {mac} is not registered." });
                }

                DateTimeOffset to = time.GetUtcNow();
                DateTimeOffset from = to.AddMinutes(-Math.Clamp(minutes ?? 15, 1, 24 * 60));
                int points = Math.Clamp(maxPoints ?? 240, 20, 2_000);

                var stopwatch = Stopwatch.StartNew();
                timeline.TryGet(device.Key, out TimelineStore<double>? store);
                List<(DateTimeOffset Timestamp, double Value)> raw = store?.Range(from, to) ?? new List<(DateTimeOffset, double)>();
                List<TimelineBucket> buckets = TimelineDownsampler.Downsample(raw, from, to, points);
                stopwatch.Stop();

                double? low = device.Metric == "soil_moisture" ? alerts.Options.MoistureCriticalBelow : null;
                double? high = device.Metric == "power_draw" ? alerts.Options.PowerCriticalAbove : null;

                return Results.Ok(new TimelineDto(
                    device.MacAddress,
                    device.DisplayName,
                    device.Metric,
                    device.Unit,
                    from,
                    to,
                    store?.Count ?? 0,
                    buckets.Count,
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds * 1000, 1),
                    buckets.Select(b => new TimelinePointDto(b.Timestamp, Math.Round(b.Mean, 3), Math.Round(b.Min, 3), Math.Round(b.Max, 3), b.Count)).ToArray(),
                    low,
                    high));
            })
            .WithTags("Command Stream");

        app.MapGet("/api/history", (int? minutes, HistoryKind? kind, int? max, TimelineService timeline, TimeProvider time) =>
            {
                DateTimeOffset now = time.GetUtcNow();
                return Results.Ok(timeline.History(
                    now.AddMinutes(-Math.Clamp(minutes ?? 120, 1, 7 * 24 * 60)),
                    now.AddMinutes(1),
                    kind,
                    Math.Clamp(max ?? 100, 1, 500)));
            })
            .WithTags("Command Stream");

        return app;
    }
}
