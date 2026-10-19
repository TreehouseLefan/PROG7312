using System.Diagnostics.CodeAnalysis;
using SmartX.Core.Contracts;
using SmartX.Core.Topology;

namespace SmartX.Api.Services;

/// <summary>Owns the live Facility → Zone → Sub-Zone → Node tree.</summary>
public sealed class TopologyService
{
    private readonly object _gate = new();
    private readonly List<DeploymentNode> _roots = new();

    public void Seed(IEnumerable<DeploymentNode> roots)
    {
        lock (_gate)
        {
            _roots.Clear();
            _roots.AddRange(roots);
        }
    }

    /// <summary>Deep copy of the whole tree (safe to serialise or validate outside the lock).</summary>
    public List<DeploymentNode> Snapshot()
    {
        lock (_gate)
        {
            return _roots.Select(TopologyNavigator.Clone).ToList();
        }
    }

    public bool TryGetSubZone(string subZoneId, [NotNullWhen(true)] out GridGroupDto? group)
    {
        lock (_gate)
        {
            group = null;
            if (!TopologyNavigator.TryFindPath(_roots, subZoneId, out List<DeploymentNode> path) ||
                path.Count != 3 ||
                path[2].Level != DeploymentLevel.SubZone)
            {
                return false;
            }

            group = new GridGroupDto(path[0].Id, path[0].DisplayName, path[1].Id, path[1].DisplayName, path[2].Id, path[2].DisplayName);
            return true;
        }
    }

    /// <summary>Adds a device node under a Sub-Zone and returns the resolved location.</summary>
    public bool TryAttachDevice(
        string subZoneId,
        DeploymentNode deviceNode,
        string? room,
        [NotNullWhen(true)] out DeploymentLocationDto? location,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(deviceNode);

        lock (_gate)
        {
            location = null;
            error = string.Empty;

            if (!TopologyNavigator.TryFindPath(_roots, subZoneId, out List<DeploymentNode> path) ||
                path.Count != 3 ||
                path[2].Level != DeploymentLevel.SubZone)
            {
                error = $"'{subZoneId}' is not a known Sub-Zone.";
                return false;
            }

            if (TopologyNavigator.FindById(_roots, deviceNode.Id) is not null)
            {
                error = $"A node with id '{deviceNode.Id}' already exists.";
                return false;
            }

            path[2].Children.Add(deviceNode);
            location = new DeploymentLocationDto(
                path[0].Id,
                path[0].DisplayName,
                path[1].Id,
                path[1].DisplayName,
                path[2].Id,
                path[2].DisplayName,
                deviceNode.Id,
                string.IsNullOrWhiteSpace(room) ? null : room.Trim());
            return true;
        }
    }

    public bool RemoveNode(string nodeId)
    {
        lock (_gate)
        {
            return TopologyNavigator.Remove(_roots, nodeId);
        }
    }

    public GridGroupDto[] GetGroups()
    {
        lock (_gate)
        {
            var groups = new List<GridGroupDto>();
            foreach (DeploymentNode facility in _roots)
            {
                foreach (DeploymentNode zone in facility.Children.Where(z => z.Level == DeploymentLevel.Zone))
                {
                    foreach (DeploymentNode subZone in zone.Children.Where(s => s.Level == DeploymentLevel.SubZone))
                    {
                        groups.Add(new GridGroupDto(
                            facility.Id,
                            facility.DisplayName,
                            zone.Id,
                            zone.DisplayName,
                            subZone.Id,
                            subZone.DisplayName));
                    }
                }
            }

            return groups.ToArray();
        }
    }

    public LocationOptionDto[] GetSubZoneOptions()
    {
        lock (_gate)
        {
            var options = new List<LocationOptionDto>();
            foreach (DeploymentNode facility in _roots)
            {
                foreach (DeploymentNode zone in facility.Children.Where(z => z.Level == DeploymentLevel.Zone))
                {
                    foreach (DeploymentNode subZone in zone.Children.Where(s => s.Level == DeploymentLevel.SubZone))
                    {
                        int limit = EffectiveInterval(facility, zone, subZone);
                        options.Add(new LocationOptionDto(
                            subZone.Id,
                            $"{facility.Name} › {zone.Name} › {subZone.Name} ({subZone.Description})",
                            facility.Id,
                            zone.Id,
                            TopologyNavigator.CountDevices(subZone),
                            limit));
                    }
                }
            }

            return options.ToArray();
        }
    }

    private static int EffectiveInterval(params DeploymentNode[] chain)
    {
        int limit = 3600;
        foreach (DeploymentNode node in chain)
        {
            if (node.Profile?.MaxSampleIntervalSeconds is int seconds && seconds > 0 && seconds < limit)
            {
                limit = seconds;
            }
        }

        return limit;
    }
}
