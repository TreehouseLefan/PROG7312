using System.Text.Json;
using SmartX.Core.Pulse;
using SmartX.Core.Streaming;

namespace SmartX.Core.Contracts;

/// <summary>One point on a device sparkline.</summary>
public sealed record TelemetryPointDto(DateTimeOffset Timestamp, double Value);

/// <summary>
/// A single reading pushed by a device. <see cref="PayloadType"/> is "float", "int" or "bool"
/// and <see cref="Value"/> is kept as raw JSON until the gateway knows which generic type to use.
/// </summary>
public sealed record TelemetryIngestRequest(
    string DeviceMac,
    string PayloadType,
    JsonElement Value,
    DateTimeOffset? Timestamp);

/// <summary>
/// Returned with 202 Accepted: the reading passed validation and is waiting in a queue.
/// <see cref="Lane"/> says whether it went to the FIFO lane or jumped to the priority lane.
/// </summary>
public sealed record TelemetryIngestResult(
    bool Accepted,
    string DeviceMac,
    string Metric,
    double Value,
    DateTimeOffset Timestamp,
    IngestLane Lane,
    string? PriorityReason,
    long Sequence,
    int StandardDepth,
    int PriorityDepth);

/// <summary>
/// Historical upload: a jagged array where every inner array is one batch of raw readings.
/// Batches may have different lengths (for example after a device reconnects).
/// </summary>
public sealed record TelemetryBatchRequest(
    string DeviceMac,
    string PayloadType,
    JsonElement[][] Batches,
    int IntervalSeconds,
    DateTimeOffset? FirstTimestamp);

public sealed record BatchSummaryDto(int BatchIndex, int Count, double? Min, double? Max, double? Mean);

public sealed record TelemetryBatchResult(
    string DeviceMac,
    int BatchCount,
    int ReadingsAccepted,
    int ReadingsRejected,
    int ListCapacity,
    double ElapsedMilliseconds,
    long AllocatedBytes,
    BatchSummaryDto[] Batches);

public sealed record IngestStatsDto(
    long Accepted,
    long Rejected,
    double AcceptedPerSecond,
    int RegisteredDevices,
    DateTimeOffset StartedAt);

public sealed record DeviceTelemetryDto(
    string Mac,
    TelemetryPointDto[] Points,
    double Mean,
    double StandardDeviation,
    double BandLow,
    double BandHigh,
    double ZLimit,
    long TotalReceived,
    PulseTileDto? Tile);

public sealed record HistoryMatrixDto(
    string SubZoneId,
    int Slots,
    string[] RowLabels,
    double?[][] Cells,
    double?[][] Deviation,
    double?[] ColumnDeviation);
