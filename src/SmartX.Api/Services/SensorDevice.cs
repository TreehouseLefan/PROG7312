using SmartX.Core.Contracts;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>Server-side record of a registered device.</summary>
public sealed class SensorDevice
{
    private MacKey? _key;

    /// <summary>The MAC address as a 48-bit dictionary key (parsed once, then cached).</summary>
    public MacKey Key => _key ??= MacKey.Parse(MacAddress);

    public bool IsActuator => Category == SensorCategory.Actuator;

    public required string MacAddress { get; init; }
    public required string DisplayName { get; init; }
    public required SensorCategory Category { get; init; }
    public required TelemetryValueKind ValueKind { get; init; }
    public required string Metric { get; init; }
    public string Unit { get; init; } = string.Empty;
    public int ExpectedIntervalSeconds { get; init; } = 2;
    public double? MinValue { get; init; }
    public double? MaxValue { get; init; }
    public required DeploymentLocationDto Location { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public bool IsSimulated { get; init; }

    public SensorDeviceDto ToDto(int attachmentCount) => new(
        MacAddress,
        DisplayName,
        Category,
        ValueKind,
        Metric,
        Unit,
        ExpectedIntervalSeconds,
        MinValue,
        MaxValue,
        Location,
        RegisteredAt,
        attachmentCount,
        IsSimulated);
}
