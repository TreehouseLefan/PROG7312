using System.Text.Json.Serialization;
using SmartX.Core.Mesh;

namespace SmartX.Core.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<MeshNodeStatus>))]
public enum MeshNodeStatus
{
    /// <summary>Alive and has a route to the server.</summary>
    Up,

    /// <summary>Failed (manually, or disconnected according to the Part 2 alert sets).</summary>
    Down,

    /// <summary>Alive, but every path to the server runs through down nodes.</summary>
    Isolated
}

public sealed record MeshNodeDto(
    string Id,
    string? Mac,
    string Name,
    MeshNodeKind Kind,
    string FacilityId,
    string SubZoneId,
    double X,
    double Y,
    MeshNodeStatus Status,
    bool ManuallyFailed,
    bool Critical,
    string? MstParentId,
    int? MstHops,
    double? MstPathCost,
    string? ShortestParentId,
    double? ShortestPathCost,
    int Degree);

public sealed record MeshEdgeDto(
    int Index,
    string A,
    string B,
    double DistanceMetres,
    double CostMicrojoules,
    MeshLinkKind Kind,
    bool InMst,
    bool InShortestTree,
    bool Live);

public sealed record MeshAreaDto(string FacilityId, string Name, double X, double Y, double Width, double Height);

public sealed record MeshStatsDto(
    int Nodes,
    int Links,
    int Up,
    int Down,
    int Isolated,
    int MstLinks,
    double MstCostMicrojoules,
    double KruskalCostMicrojoules,
    bool PrimKruskalAgree,
    double ShortestTreeCostMicrojoules,
    double MstBottleneckMicrojoules,
    double ShortestTreeBottleneckMicrojoules,
    double MeanMstHops,
    double MeanShortestHops,
    double PrimMicroseconds,
    double KruskalMicroseconds,
    double DijkstraMicroseconds,
    long PrimHeapOperations,
    long KruskalHeapComparisons,
    int Components,
    string[] CriticalNodeIds);

public sealed record HealEventDto(
    DateTimeOffset At,
    string Action,
    string NodeId,
    string NodeName,
    int Rerouted,
    int Isolated,
    double MstCostBefore,
    double MstCostAfter,
    double RecomputeMicroseconds,
    string Detail);

public sealed record MeshSnapshotDto(
    long Version,
    DateTimeOffset ComputedAt,
    MeshNodeDto[] Nodes,
    MeshEdgeDto[] Edges,
    MeshAreaDto[] Areas,
    MeshStatsDto Stats,
    HealEventDto[] Events,
    double RadioRangeMetres,
    int PacketBits);

public sealed record RouteHopDto(
    string FromId,
    string FromName,
    string ToId,
    string ToName,
    double DistanceMetres,
    double CostMicrojoules,
    MeshLinkKind Kind);

public sealed record RouteDto(
    string Algorithm,
    bool Reachable,
    RouteHopDto[] Hops,
    int HopCount,
    double TotalCostMicrojoules,
    double BottleneckMicrojoules,
    string? ExitGatewayId);

public sealed record RouteReportDto(
    string NodeId,
    string NodeName,
    string? Mac,
    MeshNodeStatus Status,
    RouteDto Mst,
    RouteDto Shortest,
    string? NextHopId,
    string? NextHopName,
    RouteDto? IfNextHopFails,
    string Explanation);

public sealed record TraversalStepDto(
    int Step,
    string Id,
    string Name,
    MeshNodeKind Kind,
    string SubZoneId,
    int Depth,
    string? ParentId,
    bool InScope);

public sealed record HopAdviceDto(
    string NodeId,
    string NodeName,
    string UsualNextHopId,
    string UsualNextHopName,
    string? NewNextHopId,
    string? NewNextHopName,
    string Advice);

public sealed record TraversalReportDto(
    string Mode,
    string StartId,
    string StartName,
    string? ScopeSubZoneId,
    TraversalStepDto[] Order,
    int ReachableInScope,
    MeshNodeDto[] DownInScope,
    MeshNodeDto[] IsolatedInScope,
    HopAdviceDto[] HopAdvice,
    int EdgesExamined,
    double ElapsedMicroseconds,
    string Summary);

public sealed record MeshBenchmarkDto(
    int Vertices,
    int Edges,
    int Reached,
    double BuildMilliseconds,
    double BfsMilliseconds,
    double PrimMilliseconds,
    double KruskalMilliseconds,
    double DijkstraMilliseconds,
    double PrimCost,
    double KruskalCost,
    bool CostsAgree,
    double ShortestTreeCost,
    long PrimHeapOperations,
    long KruskalHeapComparisons);
