using SmartX.Core.Topology;

namespace SmartX.Core.Tests;

public class TopologyValidatorTests
{
    private static List<DeploymentNode> ValidTree() => new()
    {
        new DeploymentNode
        {
            Id = "fac-a",
            Name = "Facility A",
            Level = DeploymentLevel.Facility,
            Profile = new ConfigProfile { MaxSampleIntervalSeconds = 30, EncryptionRequired = true },
            Children =
            {
                new DeploymentNode
                {
                    Id = "zone-1",
                    Name = "Zone 1",
                    Level = DeploymentLevel.Zone,
                    Profile = new ConfigProfile { MaxSampleIntervalSeconds = 10 },
                    Children =
                    {
                        new DeploymentNode
                        {
                            Id = "sz-b",
                            Name = "Sub-Zone B",
                            Level = DeploymentLevel.SubZone,
                            Children =
                            {
                                new DeploymentNode { Id = "node-1", Name = "Node B-01", Level = DeploymentLevel.Node, DeviceMac = "24:0A:C4:0B:00:01" }
                            }
                        }
                    }
                }
            }
        }
    };

    [Fact]
    public void Valid_tree_passes()
    {
        var result = new DeploymentTreeValidator(_ => 2).Validate(ValidTree());

        Assert.True(result.IsValid);
        Assert.Equal(4, result.NodesChecked);
        Assert.Equal(1, result.DevicesChecked);
    }

    [Fact]
    public void Broken_sample_reports_every_class_of_error()
    {
        var result = new DeploymentTreeValidator().Validate(TopologySamples.BuildBrokenSample());

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Message.Contains("looser than the inherited limit"));
        Assert.Contains(result.Issues, i => i.Message.Contains("Encryption cannot be switched off"));
        Assert.Contains(result.Issues, i => i.Message.Contains("not a valid MAC"));
        Assert.Contains(result.Issues, i => i.Message.Contains("cannot have children"));
        Assert.Contains(result.Issues, i => i.Message.Contains("Duplicate node id"));
        Assert.Contains(result.Issues, i => i.Message.Contains("deployed more than once"));
        Assert.Contains(result.Issues, i => i.Message.Contains("cannot sit under a Zone"));
        Assert.Contains(result.Issues, i => i.Severity == DeploymentTreeValidator.Warning);
    }

    [Fact]
    public void Slow_device_is_rejected_by_the_inherited_interval()
    {
        var result = new DeploymentTreeValidator(_ => 30).Validate(ValidTree());

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Message.Contains("at least every 10s"));
    }

    [Fact]
    public void Cycles_are_detected_without_stack_overflow()
    {
        var facility = new DeploymentNode { Id = "f", Name = "F", Level = DeploymentLevel.Facility };
        var zone = new DeploymentNode { Id = "z", Name = "Z", Level = DeploymentLevel.Zone };
        facility.Children.Add(zone);
        zone.Children.Add(facility); // loop back

        var result = new DeploymentTreeValidator().Validate(new List<DeploymentNode> { facility });

        Assert.Contains(result.Issues, i => i.Message.Contains("Cycle detected"));
    }

    [Fact]
    public void Placement_check_reports_the_chain_bottom_up()
    {
        var placement = new DeploymentTreeValidator(_ => 2).CheckPlacement(ValidTree(), "node-1");

        Assert.True(placement.Found);
        Assert.True(placement.IsSafe);
        Assert.Equal("Node B-01 -> Sub-Zone B -> Zone 1 -> Facility A", placement.Chain);
        Assert.Equal(10, placement.Profile!.MaxSampleIntervalSeconds);
        Assert.True(placement.Profile.EncryptionRequired);
    }

    [Fact]
    public void Navigator_finds_counts_and_removes_nodes_recursively()
    {
        List<DeploymentNode> tree = ValidTree();

        Assert.True(TopologyNavigator.TryFindPath(tree, "node-1", out List<DeploymentNode> path));
        Assert.Equal(4, path.Count);
        Assert.Equal(1, TopologyNavigator.CountDevices(tree[0]));
        Assert.True(TopologyNavigator.Remove(tree, "node-1"));
        Assert.Equal(0, TopologyNavigator.CountDevices(tree[0]));
        Assert.False(TopologyNavigator.TryFindPath(tree, "node-1", out _));
    }
}
