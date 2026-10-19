using System.Text.Json.Serialization;

namespace SmartX.Core.Contracts;

/// <summary>Manual override commands an operator can send to actuators (pumps, valves, relays).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CommandAction>))]
public enum CommandAction
{
    TurnOn,
    TurnOff,
    Toggle,
    CloseAllValves,
    OpenAllValves
}

/// <summary>
/// Body of POST /api/commands. <see cref="DeviceMac"/> targets one actuator; <see cref="SubZoneId"/>
/// scopes the bulk valve commands (omit it to target every valve).
/// <see cref="SuggestionId"/> is set when the command came from a "Suggested Action".
/// </summary>
public sealed record IssueCommandRequest(
    CommandAction Action,
    string? DeviceMac,
    string? SubZoneId,
    string? IssuedBy,
    string? SuggestionId);

public sealed record CommandChangeDto(string Mac, string DeviceName, bool Before, bool After);

public sealed record CommandRecordDto(
    Guid Id,
    CommandAction Action,
    string Label,
    string IssuedBy,
    string Source,
    DateTimeOffset IssuedAt,
    CommandChangeDto[] Changes,
    string Status,
    DateTimeOffset? ConfirmedAt,
    double? ConfirmLatencyMs,
    Guid? RevertsId);

public sealed record CommandHistoryDto(
    CommandRecordDto[] UndoStack,
    CommandRecordDto[] RedoStack,
    CommandRecordDto[] Recent,
    int Capacity);

public sealed record CommandResultDto(bool Success, string Message, CommandRecordDto? Record, CommandHistoryDto History);

/// <summary>
/// Device shadow (digital twin) for an actuator: the state the operator wants (Desired) and the state the
/// device last reported in its telemetry (Reported). Devices read their desired state from
/// GET /api/commands/shadows, the same way a physical ESP32 would poll the gateway.
/// </summary>
public sealed record ActuatorShadowDto(
    string Mac,
    string Name,
    string Metric,
    string SubZoneId,
    bool Desired,
    bool? Reported,
    DateTimeOffset? ReportedAt,
    bool InSync);

/// <summary>Command history plus every actuator shadow, pushed together when either changes.</summary>
public sealed record CommandStateDto(CommandHistoryDto History, ActuatorShadowDto[] Shadows);
