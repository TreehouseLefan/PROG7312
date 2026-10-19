namespace SmartX.Core.Topology;

/// <summary>A deliberately broken tree used to demonstrate the recursive validator.</summary>
public static class TopologySamples
{
    public static List<DeploymentNode> BuildBrokenSample()
    {
        var facility = new DeploymentNode
        {
            Id = "fac-demo",
            Name = "Facility Demo",
            Description = "Intentionally misconfigured",
            Level = DeploymentLevel.Facility,
            Profile = new ConfigProfile { MaxSampleIntervalSeconds = 30, EncryptionRequired = true }
        };

        var zone = new DeploymentNode
        {
            Id = "zone-demo",
            Name = "Zone 9",
            Level = DeploymentLevel.Zone,
            Profile = new ConfigProfile { MaxSampleIntervalSeconds = 10 }
        };

        // Error: looser interval than its Zone, and tries to disable encryption.
        var looseSubZone = new DeploymentNode
        {
            Id = "sz-loose",
            Name = "Sub-Zone Loose",
            Level = DeploymentLevel.SubZone,
            Profile = new ConfigProfile { MaxSampleIntervalSeconds = 60, EncryptionRequired = false }
        };

        // Error: invalid MAC, and a device node with children.
        var badDevice = new DeploymentNode
        {
            Id = "node-bad-mac",
            Name = "Node Bad MAC",
            Level = DeploymentLevel.Node,
            DeviceMac = "ZZ:12:34",
            Children =
            {
                new DeploymentNode { Id = "node-child", Name = "Node Nested", Level = DeploymentLevel.Node, DeviceMac = "24:0A:C4:99:00:01" }
            }
        };

        // Error: min above max.
        var badRange = new DeploymentNode
        {
            Id = "node-bad-range",
            Name = "Node Bad Range",
            Level = DeploymentLevel.Node,
            DeviceMac = "24:0A:C4:99:00:02",
            Profile = new ConfigProfile { MinValue = 80, MaxValue = 20 }
        };

        // Error: duplicate id and duplicate MAC.
        var duplicate = new DeploymentNode
        {
            Id = "node-bad-range",
            Name = "Node Duplicate",
            Level = DeploymentLevel.Node,
            DeviceMac = "24:0a:c4:99:00:02"
        };

        looseSubZone.Children.Add(badDevice);
        looseSubZone.Children.Add(badRange);
        looseSubZone.Children.Add(duplicate);

        // Error: a device placed directly under a Zone (skipping the Sub-Zone tier).
        var misplaced = new DeploymentNode
        {
            Id = "node-misplaced",
            Name = "Node Misplaced",
            Level = DeploymentLevel.Node,
            DeviceMac = "24:0A:C4:99:00:03"
        };

        // Warning: an empty Sub-Zone.
        var emptySubZone = new DeploymentNode { Id = "sz-empty", Name = "Sub-Zone Empty", Level = DeploymentLevel.SubZone };

        zone.Children.Add(looseSubZone);
        zone.Children.Add(misplaced);
        zone.Children.Add(emptySubZone);
        facility.Children.Add(zone);

        return new List<DeploymentNode> { facility };
    }
}
