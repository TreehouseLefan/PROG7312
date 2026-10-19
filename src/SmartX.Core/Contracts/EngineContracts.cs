namespace SmartX.Core.Contracts;

/// <summary>A "Suggested Action" shown on the dashboard, with the evidence behind it.</summary>
public sealed record SuggestionDto(
    string Id,
    string Title,
    string Reason,
    double Confidence,
    double Support,
    string Source,
    string ActionKey,
    string Kind,
    string? DeviceMac,
    string? DeviceName,
    CommandAction? Command,
    string? Query,
    string[] Triggers);

/// <summary>An "Automated Insight": a device the engine thinks needs attention before anyone searches for it.</summary>
public sealed record InsightDto(
    string Id,
    string Kind,
    string Severity,
    string Title,
    string Detail,
    string? DeviceMac,
    double? ProjectedSeconds);

public sealed record EngineStatsDto(
    long ContextsObserved,
    long ActionsObserved,
    int RulesLearned,
    long SuggestionsShown,
    long SuggestionsAccepted,
    long ActionsAnticipated,
    double HitRate,
    double AcceptanceRate);

public sealed record EngineSnapshotDto(
    DateTimeOffset At,
    SuggestionDto[] Suggestions,
    InsightDto[] Insights,
    EngineStatsDto Stats);

public sealed record LearnedRuleDto(string Context, string Action, string Description, double Support, double Confidence, double Lift);
