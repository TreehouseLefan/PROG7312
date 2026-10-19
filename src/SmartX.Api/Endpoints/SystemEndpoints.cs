using SmartX.Core.Contracts;

namespace SmartX.Api.Endpoints;

public static class SystemEndpoints
{
    private static readonly PillarDto[] Pillars =
    {
        new(
            "telemetry",
            "Sensor Data Ingestion & Telemetry",
            "Register ESP32-class devices, validate multi-typed telemetry and watch the live Sensor Pulse Grid.",
            true,
            "Part 1 · live",
            "/telemetry"),
        new(
            "commands",
            "Real-Time Command Stream & History",
            "Watch the two-lane telemetry stream, send override commands with undo, and act on predicted suggestions.",
            true,
            "Part 2 · live",
            "/commands"),
        new(
            "mesh",
            "Network Topology & Mesh Routing",
            "Visualise the mesh and route traffic between gateways.",
            false,
            "Coming in the final PoE",
            "/mesh")
    };

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Ok(new
        {
            service = "Smart-X Data Ingestion and Validation Gateway",
            openApi = "/openapi/v1.json",
            health = "/api/health",
            pulseHub = "/hubs/pulse"
        })).ExcludeFromDescription();

        RouteGroupBuilder group = app.MapGroup("/api").WithTags("System");

        group.MapGet("/health", (TimeProvider time) => Results.Ok(new HealthDto(
            "Healthy",
            "Smart-X Ingestion Gateway",
            typeof(SystemEndpoints).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            time.GetUtcNow())));

        group.MapGet("/pillars", () => Results.Ok(Pillars));

        return app;
    }
}
