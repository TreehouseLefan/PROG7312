using System.Globalization;

namespace SmartX.Core.Readings;

/// <summary>
/// A smart-meter reading with overloaded operators so load can be aggregated and compared directly:
/// <code>
/// PowerMeterReading meter3 = meter1 + meter2;   // aggregate load
/// PowerMeterReading delta  = meter1 - meter2;   // delta comparison
/// bool heavier            = meter1 > meter2;    // logical comparison
/// </code>
/// </summary>
public readonly struct PowerMeterReading : IEquatable<PowerMeterReading>, IComparable<PowerMeterReading>
{
    public PowerMeterReading(string meterId, double watts, DateTimeOffset timestamp, int sourceCount = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meterId);

        if (!double.IsFinite(watts))
        {
            throw new ArgumentOutOfRangeException(nameof(watts), "Watts must be a finite number.");
        }

        if (sourceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceCount), "Source count cannot be negative.");
        }

        MeterId = meterId;
        Watts = watts;
        Timestamp = timestamp;
        SourceCount = sourceCount;
    }

    public string MeterId { get; }
    public double Watts { get; }
    public DateTimeOffset Timestamp { get; }

    /// <summary>How many physical meters contributed to this value.</summary>
    public int SourceCount { get; }

    public double Kilowatts => Watts / 1000d;

    public static PowerMeterReading operator +(PowerMeterReading left, PowerMeterReading right) =>
        new(
            $"{left.MeterId}+{right.MeterId}",
            left.Watts + right.Watts,
            Latest(left.Timestamp, right.Timestamp),
            left.SourceCount + right.SourceCount);

    public static PowerMeterReading operator -(PowerMeterReading left, PowerMeterReading right) =>
        new(
            $"{left.MeterId}-{right.MeterId}",
            left.Watts - right.Watts,
            Latest(left.Timestamp, right.Timestamp),
            left.SourceCount + right.SourceCount);

    public static PowerMeterReading operator *(PowerMeterReading reading, double factor) =>
        new(reading.MeterId, reading.Watts * factor, reading.Timestamp, reading.SourceCount);

    public static PowerMeterReading operator /(PowerMeterReading reading, int divisor)
    {
        if (divisor == 0)
        {
            throw new DivideByZeroException("Cannot divide a meter reading by zero.");
        }

        return new PowerMeterReading(reading.MeterId, reading.Watts / divisor, reading.Timestamp, reading.SourceCount);
    }

    public static bool operator >(PowerMeterReading left, PowerMeterReading right) => left.Watts > right.Watts;

    public static bool operator <(PowerMeterReading left, PowerMeterReading right) => left.Watts < right.Watts;

    public static bool operator >=(PowerMeterReading left, PowerMeterReading right) => left.Watts >= right.Watts;

    public static bool operator <=(PowerMeterReading left, PowerMeterReading right) => left.Watts <= right.Watts;

    public static bool operator ==(PowerMeterReading left, PowerMeterReading right) => left.Equals(right);

    public static bool operator !=(PowerMeterReading left, PowerMeterReading right) => !left.Equals(right);

    public static explicit operator double(PowerMeterReading reading) => reading.Watts;

    /// <summary>Sums any number of readings with the overloaded + operator.</summary>
    public static PowerMeterReading Total(IReadOnlyList<PowerMeterReading> readings, string label)
    {
        ArgumentNullException.ThrowIfNull(readings);

        if (readings.Count == 0)
        {
            return new PowerMeterReading(label, 0, DateTimeOffset.UtcNow, 0);
        }

        PowerMeterReading total = readings[0];
        for (int i = 1; i < readings.Count; i++)
        {
            total += readings[i];
        }

        return total.Relabel(label);
    }

    public PowerMeterReading Relabel(string meterId) => new(meterId, Watts, Timestamp, SourceCount);

    public bool IsWithin(PowerMeterReading other, double toleranceWatts) =>
        Math.Abs(Watts - other.Watts) <= Math.Abs(toleranceWatts);

    public int CompareTo(PowerMeterReading other) => Watts.CompareTo(other.Watts);

    public bool Equals(PowerMeterReading other) =>
        string.Equals(MeterId, other.MeterId, StringComparison.OrdinalIgnoreCase) &&
        Watts.Equals(other.Watts);

    public override bool Equals(object? obj) => obj is PowerMeterReading other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(MeterId ?? string.Empty), Watts);

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0}: {1:0.##} W", MeterId, Watts);

    private static DateTimeOffset Latest(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
