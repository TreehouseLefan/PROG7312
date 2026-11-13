using SmartX.Api.Services;
using SmartX.Core.Contracts;
using SmartX.Core.Mesh;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Mesh;

/// <summary>
/// Physical placement of every node, in metres, for the simulated deployment. Devices are laid out in a
/// grid per Sub Zone (sorted by name, so the layout is stable).
/// </summary>
public static class MeshLayout
{
    public const string ServerId = "server";

    private sealed record Cluster(double OriginX, double OriginY, int Columns, double Spacing);

    private sealed record GatewaySite(string Id, string Name, string FacilityId, double X, double Y);

    private static readonly Dictionary<string, Cluster> Clusters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sz-a"] = new Cluster(20, 60, 4, 20),
        ["sz-b"] = new Cluster(20, 120, 4, 20),
        ["sz-c"] = new Cluster(115, 92, 5, 16),
        ["sz-d"] = new Cluster(290, 110, 4, 20)
    };

    private static readonly GatewaySite[] Gateways =
    {
        new("gw-a1", "GW-A1 Greenhouse Gateway", "fac-a", 100, 100),
        new("gw-a2", "GW-A2 Pump House Gateway", "fac-a", 200, 70),
        new("gw-b", "GW-B Tower Gateway", "fac-b", 320, 80)
    };

    public static MeshAreaDto[] Areas { get; } =
    {
        new("fac-a", "Facility A · Stellenbosch Hydroponic Farm", 5, 45, 225, 115),
        new("fac-b", "Facility B · Sandton Utility Tracker", 275, 60, 90, 95)
    };

    public static List<MeshVertex> BuildVertices(IReadOnlyList<SensorDevice> devices)
    {
        var vertices = new List<MeshVertex>(devices.Count + Gateways.Length + 1)
        {
            new(ServerId, "Central Server", MeshNodeKind.Server, 200, 10, "cloud", string.Empty, null)
        };

        foreach (GatewaySite site in Gateways)
        {
            vertices.Add(new MeshVertex(site.Id, site.Name, MeshNodeKind.Gateway, site.X, site.Y, site.FacilityId, string.Empty, null));
        }

        foreach (IGrouping<string, SensorDevice> group in devices.GroupBy(d => d.Location.SubZoneId, StringComparer.OrdinalIgnoreCase))
        {
            List<SensorDevice> ordered = group.OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            Cluster cluster = ClusterFor(group.Key, ordered[0].Location.FacilityId);

            for (int i = 0; i < ordered.Count; i++)
            {
                SensorDevice device = ordered[i];
                double x = cluster.OriginX + ((i % cluster.Columns) * cluster.Spacing);
                double y = cluster.OriginY + ((i / cluster.Columns) * cluster.Spacing);
                vertices.Add(new MeshVertex(
                    device.Location.NodeId,
                    device.DisplayName,
                    device.Category == SensorCategory.Actuator ? MeshNodeKind.Actuator : MeshNodeKind.Sensor,
                    x,
                    y,
                    device.Location.FacilityId,
                    device.Location.SubZoneId,
                    device.MacAddress));
            }
        }

        return vertices;
    }

    /// <summary>A Sub Zone added after seeding is placed next to its facility's first gateway.</summary>
    private static Cluster ClusterFor(string subZoneId, string facilityId)
    {
        if (Clusters.TryGetValue(subZoneId, out Cluster? cluster))
        {
            return cluster;
        }

        GatewaySite? gateway = Gateways.FirstOrDefault(g => string.Equals(g.FacilityId, facilityId, StringComparison.OrdinalIgnoreCase));
        return gateway is null
            ? new Cluster(20, 170, 4, 20)
            : new Cluster(gateway.X - 20, gateway.Y + 20, 4, 15);
    }
}
