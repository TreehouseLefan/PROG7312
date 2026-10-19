using SmartX.Core.Topology;

namespace SmartX.Core.Contracts;

public sealed record HealthDto(string Status, string Service, string Version, DateTimeOffset UtcNow);

/// <summary>One of the three architectural pillars shown on the landing page.</summary>
public sealed record PillarDto(
    string Key,
    string Title,
    string Description,
    bool Enabled,
    string Availability,
    string Route);

public sealed record LocationOptionDto(
    string SubZoneId,
    string Label,
    string FacilityId,
    string ZoneId,
    int DeviceCount,
    int MaxSampleIntervalSeconds);

public sealed record ValidationIssueDto(string Severity, string NodeId, string Path, string Message);

public sealed record TopologyValidationResultDto(
    bool IsValid,
    int NodesChecked,
    int DevicesChecked,
    int ErrorCount,
    int WarningCount,
    ValidationIssueDto[] Issues);

public sealed record PlacementResultDto(
    string NodeId,
    bool Found,
    bool IsSafe,
    string Chain,
    string[] Checks,
    ValidationIssueDto[] Issues,
    EffectiveProfile? Profile);

public sealed record PowerMeterDto(string MeterId, string Name, double Watts, DateTimeOffset Timestamp, int SourceCount);

public sealed record MeterAggregationDto(
    PowerMeterDto[] Inputs,
    PowerMeterDto Aggregate,
    PowerMeterDto? Delta,
    string Comparison,
    bool WithinTolerance,
    double ToleranceWatts,
    PowerMeterDto[] SubZoneTotals);

public sealed record BoxingReportDto(
    int Count,
    long GenericBytes,
    long BoxedBytes,
    double GenericMilliseconds,
    double BoxedMilliseconds,
    double AllocationRatio,
    string Summary);

public sealed record BatchDemoDto(
    int Devices,
    int BatchesPerDevice,
    int TotalReadings,
    int JaggedRows,
    double ElapsedMilliseconds,
    int ListCapacity,
    long AllocatedBytes,
    BatchSummaryDto[] SampleSummaries,
    int MatrixRows,
    int MatrixColumns,
    double?[] MatrixColumnAverages);

public sealed record SimulatorStatusDto(
    bool Enabled,
    int SimulatedDevices,
    long PacketsGenerated,
    string[] ActiveOutages,
    string[] ActiveDisconnects,
    DateTimeOffset StartedAt);

public sealed record SimulatorToggleRequest(bool Enabled);
