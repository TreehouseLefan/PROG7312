using System.Globalization;
using SmartX.Api.Mesh;
using SmartX.Api.Services;
using SmartX.Core.Contracts;
using SmartX.Core.Mesh;

namespace SmartX.Api.Endpoints;

/// <summary>Part 3: mesh topology, routing (BFS/DFS, Prim, Kruskal, Dijkstra) and AVL-backed history.</summary>
public static class MeshEndpoints
{
    public static IEndpointRouteBuilder MapMeshEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder mesh = app.MapGroup("/api/mesh").WithTags("Mesh Routing");

        // Whole mesh: nodes, links, MST / shortest-path-tree membership, stats and self-healing log.
        mesh.MapGet("/", (MeshService service) => Results.Ok(service.Snapshot()));

        // Track one node's routing path by node id or MAC address.
        mesh.MapGet("/route/{node}", (string node, MeshService service) =>
            service.Route(node) is RouteReportDto report
                ? Results.Ok(report)
                : Results.NotFound(new { error = $"No mesh node '{node}'. Use a node id (e.g. node-a1-01) or a MAC address." }));

        // Which devices are connected to a gateway / sub-zone, which are down, and where to hop.
        mesh.MapGet("/traverse", (string? from, string? mode, string? subZone, MeshService service) =>
        {
            string start = string.IsNullOrWhiteSpace(from) ? MeshLayout.ServerId : from;
            string algorithm = string.IsNullOrWhiteSpace(mode) ? "bfs" : mode.Trim().ToLowerInvariant();
            if (algorithm is not ("bfs" or "dfs"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mode"] = new[] { "Use bfs or dfs." } });
            }

            return service.Traverse(start, algorithm, subZone) is TraversalReportDto report
                ? Results.Ok(report)
                : Results.NotFound(new { error = $"No mesh node '{start}'." });
        });

        // Self-healing demo: fail or restore a gateway / device and watch the routes recompute.
        mesh.MapPost("/nodes/{node}/fail", (string node, MeshService service) =>
            service.Fail(node).ToHttpResult(snapshot => Results.Ok(snapshot), "Node could not be failed"));

        mesh.MapPost("/nodes/{node}/restore", (string node, MeshService service) =>
            service.Restore(node).ToHttpResult(snapshot => Results.Ok(snapshot), "Node could not be restored"));

        mesh.MapPost("/reset", (MeshService service) => Results.Ok(service.Reset()));

        // Scalability check on a random geometric graph (same algorithms, larger n).
        mesh.MapGet("/benchmark", (int? nodes) =>
        {
            int n = Math.Clamp(nodes ?? 2_000, 50, 50_000);
            return Results.Ok(MeshBenchmark.Run(n));
        });

        // Historical log for one sensor: AVL range query + O(log n) summary + bucketed chart.
        app.MapGet("/api/devices/{mac}/history", (
            string mac,
            string? from,
            string? to,
            int? page,
            int? pageSize,
            string? order,
            int? buckets,
            DeviceRegistry registry,
            TelemetryHistoryService history,
            TimeProvider time) =>
        {
            if (!registry.TryGet(mac, out SensorDevice? device))
            {
                return Results.NotFound(new { error = $"No device with MAC '{mac}'." });
            }

            DateTimeOffset now = time.GetUtcNow();
            var errors = new Dictionary<string, string[]>();
            DateTimeOffset end = ParseTime(to, now, "to", errors);
            DateTimeOffset start = ParseTime(from, end - TimeSpan.FromHours(2), "from", errors);
            if (errors.Count == 0 && start > end)
            {
                errors["from"] = new[] { "'from' must be before 'to'." };
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            bool descending = !string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);
            return Results.Ok(history.Query(device, start, end, page ?? 0, pageSize ?? 25, descending, buckets ?? 48));
        }).WithTags("Mesh Routing");

        return app;
    }

    /// <summary>Accepts ISO 8601 with an offset (e.g. 2026-11-12T14:00:00+02:00); a missing offset means UTC.</summary>
    private static DateTimeOffset ParseTime(string? text, DateTimeOffset fallback, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
        {
            return parsed;
        }

        errors[field] = new[] { $"'{text}' is not a valid ISO 8601 date-time." };
        return fallback;
    }
}
