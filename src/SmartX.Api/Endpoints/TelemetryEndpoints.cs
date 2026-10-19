using SmartX.Api.Services;
using SmartX.Core.Contracts;

namespace SmartX.Api.Endpoints;

public static class TelemetryEndpoints
{
    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/telemetry").WithTags("Telemetry");

        group.MapPost("/", (TelemetryIngestRequest request, TelemetryIngestionService ingestion) =>
            ingestion.Ingest(request).ToHttpResult(
                result => Results.Accepted($"/api/devices/{Uri.EscapeDataString(result.DeviceMac)}/telemetry", result),
                "Telemetry rejected by the validation gateway"));

        group.MapPost("/batch", (TelemetryBatchRequest request, TelemetryIngestionService ingestion) =>
            ingestion.IngestBatch(request).ToHttpResult(
                result => Results.Ok(result),
                "Batch rejected by the validation gateway"));

        group.MapGet("/stats", (TelemetryIngestionService ingestion) => Results.Ok(ingestion.GetStats()));

        return app;
    }
}
