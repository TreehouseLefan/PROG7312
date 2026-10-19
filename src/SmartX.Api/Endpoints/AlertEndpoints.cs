using SmartX.Api.Services;
using SmartX.Core.Contracts;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Endpoints;

public static class AlertEndpoints
{
    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/alerts").WithTags("Alerts");

        group.MapGet("/", (AlertCenter alerts) => Results.Ok(alerts.Board()));

        // Sends one critical reading through the normal ingestion gateway (same validation, same queues).
        // kind = "moisture" (soil moisture 12 %) or "power" (6,500 W).
        group.MapPost("/simulate", (string? kind, string? mac, DeviceRegistry registry, TelemetryIngestionService ingestion, TimeProvider time) =>
        {
            bool power = string.Equals(kind, "power", StringComparison.OrdinalIgnoreCase);
            string metric = power ? "power_draw" : "soil_moisture";

            SensorDevice? device = null;
            if (!string.IsNullOrWhiteSpace(mac))
            {
                if (!registry.TryGet(mac, out device) || device.Metric != metric)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["mac"] = new[] { $"Choose a registered {metric} device for kind '{(power ? "power" : "moisture")}'." }
                    });
                }
            }
            else
            {
                device = registry.All().FirstOrDefault(d => d.Metric == metric && d.Location.SubZoneId == (power ? "sz-c" : "sz-b"))
                         ?? registry.All().FirstOrDefault(d => d.Metric == metric);
            }

            if (device is null)
            {
                return Results.Problem($"No {metric} device is registered.", statusCode: StatusCodes.Status409Conflict);
            }

            GatewayOutcome<TelemetryIngestResult> outcome = device.ValueKind == TelemetryValueKind.Int
                ? ingestion.IngestValue(device.MacAddress, 6_500, time.GetUtcNow())
                : ingestion.IngestValue(device.MacAddress, 12f, time.GetUtcNow());

            return outcome.ToHttpResult(result => Results.Accepted("/api/alerts", result), "Simulated alert rejected");
        });

        return app;
    }
}
