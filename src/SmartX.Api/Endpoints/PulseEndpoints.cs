using Microsoft.AspNetCore.SignalR;
using SmartX.Api.Hubs;
using SmartX.Api.Services;
using SmartX.Core.Contracts;

namespace SmartX.Api.Endpoints;

public static class PulseEndpoints
{
    public static IEndpointRouteBuilder MapPulseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/pulse/snapshot", (
                PulseMonitor pulse,
                TopologyService topology,
                IncidentBook incidents,
                TelemetryIngestionService ingestion,
                TimeProvider time) =>
            {
                PulseTileDto[] tiles = pulse.GetTiles();
                return Results.Ok(new PulseSnapshotDto(
                    time.GetUtcNow(),
                    tiles,
                    pulse.GetInsights(tiles),
                    ingestion.GetStats(),
                    topology.GetGroups(),
                    incidents.OpenCount,
                    incidents.MedianSecondsToAcknowledge()));
            })
            .WithTags("Pulse Grid");

        RouteGroupBuilder incidentsGroup = app.MapGroup("/api/incidents").WithTags("Incidents");

        incidentsGroup.MapGet("/", (bool? openOnly, string? mac, IncidentBook incidents) =>
            Results.Ok(incidents.List(openOnly ?? false, mac)));

        incidentsGroup.MapPost("/{id:guid}/acknowledge", async (
            Guid id,
            AcknowledgeRequest? body,
            IncidentBook incidents,
            PulseMonitor pulse,
            IHubContext<PulseHub> hub,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            IncidentDto? incident = incidents.Acknowledge(id, body?.AcknowledgedBy, time.GetUtcNow());
            if (incident is null)
            {
                return Results.NotFound(new { error = "Incident not found." });
            }

            pulse.MarkDirty(incident.DeviceMac);
            await hub.Clients.All.SendAsync(PulseHub.IncidentMethod, incident, cancellationToken);
            return Results.Ok(incident);
        });

        return app;
    }
}
