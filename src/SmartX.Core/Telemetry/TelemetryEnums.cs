using System.Text.Json.Serialization;

namespace SmartX.Core.Telemetry;

/// <summary>The three sensor families required by the brief.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SensorCategory>))]
public enum SensorCategory
{
    Environmental,
    PowerConsumption,
    Actuator
}

/// <summary>The primitive payload type a device publishes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TelemetryValueKind>))]
public enum TelemetryValueKind
{
    Float,
    Int,
    Bool
}

public static class TelemetryValueKindExtensions
{
    /// <summary>Environmental sensors send floats, power meters send ints, actuators send bools.</summary>
    public static TelemetryValueKind DefaultKind(this SensorCategory category) => category switch
    {
        SensorCategory.Environmental => TelemetryValueKind.Float,
        SensorCategory.PowerConsumption => TelemetryValueKind.Int,
        SensorCategory.Actuator => TelemetryValueKind.Bool,
        _ => TelemetryValueKind.Float
    };

    /// <summary>The name used on the wire in the "payloadType" JSON field.</summary>
    public static string ToWireName(this TelemetryValueKind kind) => kind switch
    {
        TelemetryValueKind.Float => "float",
        TelemetryValueKind.Int => "int",
        TelemetryValueKind.Bool => "bool",
        _ => "float"
    };

    public static bool TryParseWireName(string? value, out TelemetryValueKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "float":
            case "single":
            case "double":
                kind = TelemetryValueKind.Float;
                return true;
            case "int":
            case "int32":
            case "integer":
                kind = TelemetryValueKind.Int;
                return true;
            case "bool":
            case "boolean":
                kind = TelemetryValueKind.Bool;
                return true;
            default:
                kind = TelemetryValueKind.Float;
                return false;
        }
    }
}
