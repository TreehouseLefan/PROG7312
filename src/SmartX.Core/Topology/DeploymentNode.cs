using System.Text.Json.Serialization;

namespace SmartX.Core.Topology;

/// <summary>Allowed nesting: Facility → Zone → SubZone → Node (device).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeploymentLevel>))]
public enum DeploymentLevel
{
    Facility,
    Zone,
    SubZone,
    Node
}

/// <summary>Configuration that a branch of the tree imposes on everything beneath it.</summary>
public sealed class ConfigProfile
{
    /// <summary>Devices below this node must report at least this often.</summary>
    public int? MaxSampleIntervalSeconds { get; set; }

    /// <summary>Once an ancestor requires encryption, no descendant may switch it off.</summary>
    public bool? EncryptionRequired { get; set; }

    /// <summary>Physically plausible lower bound for readings below this node.</summary>
    public double? MinValue { get; set; }

    /// <summary>Physically plausible upper bound for readings below this node.</summary>
    public double? MaxValue { get; set; }
}

/// <summary>The profile that actually applies at a node after inheriting from every ancestor.</summary>
public sealed record EffectiveProfile(
    int MaxSampleIntervalSeconds,
    bool EncryptionRequired,
    double? MinValue,
    double? MaxValue)
{
    public static EffectiveProfile Default { get; } = new(3600, false, null, null);
}

/// <summary>A node in the nested device deployment tree.</summary>
public sealed class DeploymentNode
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DeploymentLevel Level { get; set; }

    /// <summary>Only set on <see cref="DeploymentLevel.Node"/> entries.</summary>
    public string? DeviceMac { get; set; }

    public ConfigProfile? Profile { get; set; }

    public List<DeploymentNode> Children { get; set; } = new();

    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : $"{Name} · {Description}";
}
