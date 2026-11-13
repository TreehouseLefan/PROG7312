namespace SmartX.Core.Contracts;

public sealed record HistoryItemDto(DateTimeOffset Timestamp, double Value);

public sealed record HistorySummaryDto(long Count, double? Min, double? Max, double? Mean);

public sealed record HistoryBucketDto(DateTimeOffset From, DateTimeOffset To, long Count, double? Min, double? Max, double? Mean);

/// <summary>Proof of efficiency: how much of the AVL tree a query touched compared with a linear scan.</summary>
public sealed record HistoryQueryStatsDto(
	int TreeSize,
	int TreeHeight,
	int MaxAvlHeight,
	long Rotations,
	int PageNodesVisited,
	int SummaryNodesVisited,
	int ChartNodesVisited,
	int LinearScanComparisons,
	double ElapsedMicroseconds);

public sealed record HistoryPageDto(
	string Mac,
	string Name,
	string Metric,
	string Unit,
	DateTimeOffset From,
	DateTimeOffset To,
	int Total,
	int Page,
	int PageSize,
	bool Descending,
	HistoryItemDto[] Items,
	HistorySummaryDto Summary,
	HistoryBucketDto[] Buckets,
	HistoryQueryStatsDto Stats,
	DateTimeOffset? OldestAvailable,
	DateTimeOffset? NewestAvailable);
