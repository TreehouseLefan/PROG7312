using System.Text.Json.Serialization;
using SmartX.Core.Pulse;
using SmartX.Core.Streaming;

namespace SmartX.Core.Contracts;

/// <summary>One telemetry packet after it has been dequeued and processed.</summary>
public sealed record StreamPacketDto(
    long Sequence,
    string Mac,
    string Name,
    string Metric,
    string Unit,
    string Kind,
    double Value,
    DateTimeOffset Timestamp,
    IngestLane Lane,
    AlertSeverity? Severity,
    string? PriorityReason,
    double WaitMs,
    string SubZoneId);

public sealed record QueueStatsDto(
    int StandardDepth,
    int PriorityDepth,
    int StandardCapacity,
    long StandardProcessed,
    long PriorityProcessed,
    long Shed,
    double StandardAvgWaitMs,
    double PriorityAvgWaitMs,
    double StandardP95WaitMs,
    double PriorityP95WaitMs,
    double ProcessedPerSecond,
    bool Paused);

/// <summary>Pushed over SignalR every 500 ms to the Command Stream page.</summary>
public sealed record StreamBatchDto(DateTimeOffset Timestamp, StreamPacketDto[] Packets, QueueStatsDto Queue);

/// <summary>Result of the "priority bypass" proof: N standard packets then one critical packet.</summary>
public sealed record BurstReportDto(
    int StandardEnqueued,
    string CriticalDevice,
    int CriticalProcessedPosition,
    double CriticalWaitMs,
    double StandardAvgWaitMs,
    double StandardMaxWaitMs,
    double TotalMs,
    string Summary);

[JsonConverter(typeof(JsonStringEnumConverter<AlertType>))]
public enum AlertType
{
    CriticalMoisture,
    PowerSurge,
    Disconnected,
    Spike,
    Chatter,
    OutOfRange
}

public sealed record AlertDto(
    string Key,
    string Mac,
    string DeviceName,
    string SubZoneId,
    AlertType Type,
    AlertSeverity Severity,
    string Message,
    DateTimeOffset RaisedAt,
    DateTimeOffset LastSeenAt,
    long DuplicatesSuppressed);

/// <summary>
/// Everything the HashSets know: active unique alerts, the disconnected-node set, and how many duplicate
/// reports were absorbed instead of being shown again.
/// </summary>
public sealed record AlertBoardDto(
    AlertDto[] Active,
    string[] DisconnectedMacs,
    long Raised,
    long DuplicatesSuppressed,
    long Cleared);

public sealed record DeviceSearchResultDto(
    SensorDeviceDto Device,
    PulseState State,
    double LastValue,
    AlertType[] ActiveAlerts,
    bool? ActuatorDesired,
    double Score);

public sealed record DeviceSearchResponseDto(
    string? Query,
    int Matched,
    int Returned,
    double ElapsedMicroseconds,
    string Strategy,
    DeviceSearchResultDto[] Results);

public sealed record TimelinePointDto(DateTimeOffset Timestamp, double Value, double Min, double Max, int Count);

public sealed record TimelineDto(
    string Mac,
    string Name,
    string Metric,
    string Unit,
    DateTimeOffset From,
    DateTimeOffset To,
    int StoredPoints,
    int ReturnedPoints,
    double ElapsedMicroseconds,
    TimelinePointDto[] Points,
    double? CriticalLow,
    double? CriticalHigh);

[JsonConverter(typeof(JsonStringEnumConverter<HistoryKind>))]
public enum HistoryKind
{
    Command,
    Undo,
    Redo,
    Alert,
    AlertCleared,
    Search,
    Suggestion
}

public sealed record HistoryEventDto(DateTimeOffset At, HistoryKind Kind, string Title, string Detail, string? Mac);

public sealed record LookupBenchmarkDto(
    int Devices,
    int Lookups,
    double DictionaryMs,
    double ListScanMs,
    double Speedup,
    int DistinctHashCodes,
    int HashCollisions,
    int NaiveHashCollisions,
    string Summary);
