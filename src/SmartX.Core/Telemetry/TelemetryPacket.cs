namespace SmartX.Core.Telemetry;

/// <summary>
/// Non-generic view of a packet so different payload types can be handled uniformly.
/// Because <see cref="TelemetryPacket{T}"/> is a class, storing it behind this interface
/// does not box the payload value.
/// </summary>
public interface ITelemetryPacket
{
    string DeviceMac { get; }
    SensorCategory Category { get; }
    string Metric { get; }
    string Unit { get; }
    DateTimeOffset Timestamp { get; }
    TelemetryValueKind ValueKind { get; }
    double NumericValue { get; }
}

/// <summary>
/// Reusable generic wrapper for one telemetry reading.
/// <c>TelemetryPacket&lt;float&gt;</c> carries soil moisture, <c>TelemetryPacket&lt;int&gt;</c> carries
/// power wattage and <c>TelemetryPacket&lt;bool&gt;</c> carries valve / switch states.
/// The <c>struct</c> constraint keeps the value stored inline, and <c>IEquatable&lt;T&gt;</c>
/// lets us compare values through <see cref="EqualityComparer{T}.Default"/> without boxing.
/// </summary>
public sealed class TelemetryPacket<T> : ITelemetryPacket where T : struct, IEquatable<T>
{
    public TelemetryPacket(
        string deviceMac,
        SensorCategory category,
        string metric,
        T value,
        DateTimeOffset timestamp,
        string unit = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceMac);
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);

        if (!TelemetryValueConverter.IsSupported<T>())
        {
            throw new NotSupportedException($"Telemetry payload type '{typeof(T).Name}' is not supported.");
        }

        DeviceMac = deviceMac;
        Category = category;
        Metric = metric;
        Value = value;
        Timestamp = timestamp;
        Unit = unit ?? string.Empty;
    }

    public string DeviceMac { get; }
    public SensorCategory Category { get; }
    public string Metric { get; }
    public string Unit { get; }
    public DateTimeOffset Timestamp { get; }

    /// <summary>The strongly typed payload (float, int or bool).</summary>
    public T Value { get; }

    public TelemetryValueKind ValueKind => TelemetryValueConverter.KindOf<T>();

    public double NumericValue => TelemetryValueConverter.ToDouble(Value);

    public bool HasSameValueAs(TelemetryPacket<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return EqualityComparer<T>.Default.Equals(Value, other.Value);
    }

    public TelemetryPacket<T> WithValue(T value, DateTimeOffset timestamp) =>
        new(DeviceMac, Category, Metric, value, timestamp, Unit);

    public override string ToString() => $"{DeviceMac} {Metric}={Value}{Unit} @ {Timestamp:O}";
}
