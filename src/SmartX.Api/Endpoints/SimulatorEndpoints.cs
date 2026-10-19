using SmartX.Api.Services;
using SmartX.Api.Simulation;
using SmartX.Core.Contracts;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Endpoints;

public static class SimulatorEndpoints
{
    public static IEndpointRouteBuilder MapSimulatorEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/simulator").WithTags("Simulator");

        group.MapGet("/status", (SimulatorControl control, DeviceRegistry registry, TimeProvider time) =>
            Results.Ok(Status(control, registry, time)));

        group.MapPost("/toggle", (SimulatorToggleRequest body, SimulatorControl control, DeviceRegistry registry, TimeProvider time) =>
        {
            control.Enabled = body.Enabled;
            return Results.Ok(Status(control, registry, time));
        });

        group.MapPost("/outage/{subZoneId}", (
            string subZoneId,
            int? seconds,
            SimulatorControl control,
            TopologyService topology,
            DeviceRegistry registry,
            TimeProvider time) =>
        {
            if (!topology.TryGetSubZone(subZoneId, out GridGroupDto? subZone))
            {
                return Results.NotFound(new { error = $"Sub-Zone '{subZoneId}' does not exist." });
            }

            control.StartOutage(subZone.SubZoneId, TimeSpan.FromSeconds(Math.Clamp(seconds ?? 30, 5, 600)), time.GetUtcNow());
            return Results.Ok(Status(control, registry, time));
        });

        // Fast dry-out of a soil model (sz-a or sz-b): a critical moisture alert follows within ~30 s.
        group.MapPost("/dryout/{subZoneId}", (string subZoneId, SimWorld world, SimulatorControl control, DeviceRegistry registry, TimeProvider time) =>
        {
            if (!world.HasSoilModel(subZoneId))
            {
                return Results.NotFound(new { error = "Only sz-a and sz-b have a soil moisture model." });
            }

            world.DryOut(subZoneId, time.GetUtcNow());
            return Results.Ok(Status(control, registry, time));
        });

        group.MapPost("/spike/{mac}", (string mac, int? readings, SimulatorControl control, DeviceRegistry registry, TimeProvider time) =>
        {
            if (!registry.TryGet(mac, out SensorDevice? device))
            {
                return Results.NotFound(new { error = $"Device {mac} is not registered." });
            }

            int count = Math.Clamp(readings ?? 4, 1, 50);
            if (device.ValueKind == TelemetryValueKind.Bool)
            {
                count = Math.Max(count, 12); // chatter needs a full window of toggles
            }

            control.QueueSpike(device.MacAddress, count);
            return Results.Ok(Status(control, registry, time));
        });

        group.MapPost("/disconnect/{mac}", (string mac, int? seconds, SimulatorControl control, DeviceRegistry registry, TimeProvider time) =>
        {
            if (!registry.TryGet(mac, out SensorDevice? device))
            {
                return Results.NotFound(new { error = $"Device {mac} is not registered." });
            }

            control.Disconnect(device.MacAddress, TimeSpan.FromSeconds(Math.Clamp(seconds ?? 25, 5, 600)), time.GetUtcNow());
            return Results.Ok(Status(control, registry, time));
        });

        return app;
    }

    private static SimulatorStatusDto Status(SimulatorControl control, DeviceRegistry registry, TimeProvider time) =>
        control.Status(registry.All().Count(d => d.IsSimulated), time.GetUtcNow());
}
