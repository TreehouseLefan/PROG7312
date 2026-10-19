using SmartX.Core.Pulse;
using SmartX.Core.Telemetry;

namespace SmartX.Core.Contracts;

public sealed record PulseTileDto(
    string Mac,
    string Name,
    SensorCategory Category,
    string Metric,
    string Unit,
    string FacilityId,
    string ZoneId,
    string SubZoneId,
    PulseState State,
    double LastValue,
    double ZScore,
    double Phi,
    DateTimeOffset? LastSeen,
    DateTimeOffset StateChangedAt,
    Guid? OpenIncidentId,
    bool Acknowledged);

public sealed record ZoneInsightDto(
    string SubZoneId,
    string Label,
    int Total,
    int Healthy,
    int Spike,
    int Fading,
    int Offline,
    string Severity,
    string Message);

public sealed record GridGroupDto(
    string FacilityId,
    string FacilityName,
    string ZoneId,
    string ZoneName,
    string SubZoneId,
    string SubZoneName);

/// <summary>Pushed over SignalR once per second: only tiles that changed are included.</summary>
public sealed record PulseBatchDto(
    DateTimeOffset Timestamp,
    PulseTileDto[] Changes,
    ZoneInsightDto[] Insights,
    IngestStatsDto Stats,
    int OpenIncidents,
    double? MedianSecondsToAcknowledge);

public sealed record PulseSnapshotDto(
    DateTimeOffset Timestamp,
    PulseTileDto[] Tiles,
    ZoneInsightDto[] Insights,
    IngestStatsDto Stats,
    GridGroupDto[] Groups,
    int OpenIncidents,
    double? MedianSecondsToAcknowledge);

public sealed record IncidentDto(
    Guid Id,
    string DeviceMac,
    string DeviceName,
    string SubZoneId,
    IncidentKind Kind,
    DateTimeOffset OpenedAt,
    DateTimeOffset LastEventAt,
    int Occurrences,
    bool IsOpen,
    DateTimeOffset? ClosedAt,
    bool Acknowledged,
    string? AcknowledgedBy,
    DateTimeOffset? AcknowledgedAt,
    double? SecondsToAcknowledge);

public sealed record AcknowledgeRequest(string? AcknowledgedBy);
