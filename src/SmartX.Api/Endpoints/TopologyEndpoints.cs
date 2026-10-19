using SmartX.Api.Services;
using SmartX.Core.Topology;

namespace SmartX.Api.Endpoints;

public static class TopologyEndpoints
{
    public static IEndpointRouteBuilder MapTopologyEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/topology").WithTags("Topology");

        group.MapGet("/", (TopologyService topology) => Results.Ok(topology.Snapshot()));

        group.MapGet("/locations", (TopologyService topology) => Results.Ok(topology.GetSubZoneOptions()));

        group.MapGet("/validate", (TopologyService topology, DeviceRegistry registry) =>
            Results.Ok(new DeploymentTreeValidator(registry.ExpectedIntervalFor).Validate(topology.Snapshot())));

        // Validate any tree the user posts (for example the broken sample after editing it).
        group.MapPost("/validate", (List<DeploymentNode> roots) =>
            Results.Ok(new DeploymentTreeValidator().Validate(roots)));

        group.MapGet("/samples/invalid", () => Results.Ok(TopologySamples.BuildBrokenSample()));

        group.MapGet("/nodes/{nodeId}/placement", (string nodeId, TopologyService topology, DeviceRegistry registry) =>
            Results.Ok(new DeploymentTreeValidator(registry.ExpectedIntervalFor).CheckPlacement(topology.Snapshot(), nodeId)));

        return app;
    }
}
