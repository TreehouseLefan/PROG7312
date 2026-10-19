using System.ComponentModel.DataAnnotations;
using SmartX.Core.Telemetry;

namespace SmartX.Core.Contracts;

/// <summary>Form model used by the dashboard and validated again by the API.</summary>
public sealed class RegisterDeviceRequest
{
    [Required(ErrorMessage = "A MAC address is required.")]
    [RegularExpression("^([0-9A-Fa-f]{2}[:-]?){5}[0-9A-Fa-f]{2}$",
        ErrorMessage = "Use six hex pairs, e.g. 24:0A:C4:12:34:56.")]
    public string MacAddress { get; set; } = string.Empty;

    [Required(ErrorMessage = "Give the device a display name.")]
    [StringLength(60, MinimumLength = 3)]
    public string DisplayName { get; set; } = string.Empty;

    public SensorCategory Category { get; set; } = SensorCategory.Environmental;

    [Required(ErrorMessage = "Choose where the device is deployed.")]
    public string SubZoneId { get; set; } = string.Empty;

    [StringLength(60)]
    public string? Room { get; set; }

    [Required]
    [StringLength(40)]
    public string Metric { get; set; } = "soil_moisture";

    [StringLength(12)]
    public string Unit { get; set; } = "%";

    [Range(1, 3600, ErrorMessage = "The reporting interval must be between 1 and 3600 seconds.")]
    public int ExpectedIntervalSeconds { get; set; } = 2;

    public double? MinValue { get; set; } = 0;

    public double? MaxValue { get; set; } = 100;

    /// <summary>When true the built-in simulator publishes readings for this device.</summary>
    public bool SimulateTelemetry { get; set; } = true;
}

public sealed record DeploymentLocationDto(
    string FacilityId,
    string FacilityName,
    string ZoneId,
    string ZoneName,
    string SubZoneId,
    string SubZoneName,
    string NodeId,
    string? Room);

public sealed record SensorDeviceDto(
    string MacAddress,
    string DisplayName,
    SensorCategory Category,
    TelemetryValueKind ValueKind,
    string Metric,
    string Unit,
    int ExpectedIntervalSeconds,
    double? MinValue,
    double? MaxValue,
    DeploymentLocationDto Location,
    DateTimeOffset RegisteredAt,
    int AttachmentCount,
    bool IsSimulated);

public sealed record AttachmentDto(
    Guid Id,
    string DeviceMac,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Kind,
    string Sha256,
    DateTimeOffset UploadedAt);

public static class AttachmentKinds
{
    public const string Configuration = "Configuration";
    public const string DeploymentPhoto = "DeploymentPhoto";
    public const string HardwareLog = "HardwareLog";
    public const string Other = "Other";

    public static readonly string[] All = { Configuration, DeploymentPhoto, HardwareLog, Other };
}
