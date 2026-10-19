using System.Text.Json.Serialization;

namespace SmartX.Core.Pulse;

/// <summary>Tile states of the Sensor Pulse Grid (see the Task 1 report, Figure 1).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PulseState>))]
public enum PulseState
{
    Unknown,
    Healthy,
    Spike,
    Fading,
    Offline
}

[JsonConverter(typeof(JsonStringEnumConverter<IncidentKind>))]
public enum IncidentKind
{
    Spike,
    Offline,
    Chatter
}
