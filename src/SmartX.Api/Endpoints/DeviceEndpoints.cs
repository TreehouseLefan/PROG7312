using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;
using SmartX.Core.Contracts;
using SmartX.Core.Readings;
using SmartX.Core.Telemetry;
using SmartX.Core.Topology;

namespace SmartX.Api.Endpoints;

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/devices").WithTags("Devices");

        group.MapGet("/", (DeviceRegistry registry, AttachmentStorage attachments) =>
            Results.Ok(registry.All().Select(d => d.ToDto(attachments.CountFor(d.MacAddress))).ToArray()));

        group.MapGet("/{mac}", (string mac, DeviceRegistry registry, AttachmentStorage attachments) =>
        {
            if (!registry.TryGet(mac, out SensorDevice? device))
            {
                return Results.NotFound(new { error = $"Device {mac} is not registered." });
            }

            return Results.Ok(device.ToDto(attachments.CountFor(device.MacAddress)));
        });

        group.MapPost("/", (RegisterDeviceRequest request, DeviceRegistrationService registration) =>
            registration.Register(request).ToHttpResult(
                device => Results.Created($"/api/devices/{Uri.EscapeDataString(device.MacAddress)}", device.ToDto(0)),
                "Device registration rejected"));

        group.MapGet("/{mac}/telemetry", (
            string mac,
            int? max,
            DeviceRegistry registry,
            TelemetryStore store,
            PulseMonitor pulse) =>
        {
            if (!registry.TryGet(mac, out SensorDevice? device))
            {
                return Results.NotFound(new { error = $"Device {mac} is not registered." });
            }

            int take = Math.Clamp(max ?? 120, 10, TelemetryStore.SeriesCapacity);
            TelemetryPointDto[] points = Array.Empty<TelemetryPointDto>();
            long total = 0;

            if (store.TryGet(device.MacAddress, out ITelemetrySeries? series))
            {
                points = series.GetRecent(take);
                total = series.TotalReceived;
            }

            pulse.TryGetBaseline(device.MacAddress, out RunningStats stats);
            pulse.TryGetTile(device.MacAddress, out PulseTileDto? tile);

            double z = pulse.Options.ZScoreLimit;
            double mean = stats.Mean;
            double sd = stats.StandardDeviation;

            return Results.Ok(new DeviceTelemetryDto(
                device.MacAddress,
                points,
                Math.Round(mean, 3),
                Math.Round(sd, 3),
                Math.Round(mean - (z * sd), 3),
                Math.Round(mean + (z * sd), 3),
                z,
                total,
                tile));
        });

        group.MapGet("/{mac}/placement", (string mac, DeviceRegistry registry, TopologyService topology) =>
        {
            if (!registry.TryGet(mac, out SensorDevice? device))
            {
                return Results.NotFound(new { error = $"Device {mac} is not registered." });
            }

            var validator = new DeploymentTreeValidator(registry.ExpectedIntervalFor);
            return Results.Ok(validator.CheckPlacement(topology.Snapshot(), device.Location.NodeId));
        });

        group.MapGet("/{mac}/attachments", (string mac, DeviceRegistry registry, AttachmentStorage storage) =>
        {
            if (!registry.TryGet(mac, out SensorDevice? device))
            {
                return Results.NotFound(new { error = $"Device {mac} is not registered." });
            }

            return Results.Ok(storage.ListFor(device.MacAddress));
        });

        group.MapPost("/{mac}/attachments", async (
                string mac,
                IFormFile file,
                [FromForm] string? kind,
                DeviceRegistry registry,
                AttachmentStorage storage,
                CancellationToken cancellationToken) =>
            {
                if (!registry.TryGet(mac, out SensorDevice? device))
                {
                    return Results.NotFound(new { error = $"Device {mac} is not registered." });
                }

                GatewayOutcome<AttachmentDto> outcome = await storage.SaveAsync(device, file, kind, cancellationToken);
                return outcome.ToHttpResult(
                    info => Results.Created($"/api/attachments/{info.Id}/content", info),
                    "Upload rejected");
            })
            .DisableAntiforgery(); // token-less API called cross-origin by the dashboard

        app.MapGet("/api/attachments/{id:guid}/content", (Guid id, AttachmentStorage storage) =>
            {
                if (!storage.TryGet(id, out StoredAttachment? stored) || !File.Exists(stored.PhysicalPath))
                {
                    return Results.NotFound(new { error = "Attachment not found." });
                }

                return Results.File(
                    stored.PhysicalPath,
                    stored.Info.ContentType,
                    stored.Info.FileName,
                    enableRangeProcessing: true);
            })
            .WithTags("Devices");

        return app;
    }
}
