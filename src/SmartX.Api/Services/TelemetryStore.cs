using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

public sealed class TelemetryTypeMismatchException : Exception
{
    public TelemetryTypeMismatchException(string mac, TelemetryValueKind stored, TelemetryValueKind attempted)
        : base($"Device {mac} stores '{stored.ToWireName()}' telemetry, not '{attempted.ToWireName()}'.")
    {
    }
}

/// <summary>
/// Holds one strongly typed <see cref="TelemetrySeries{T}"/> per device behind the
/// non-generic <see cref="ITelemetrySeries"/> interface.
/// </summary>
public sealed class TelemetryStore
{
    public const int SeriesCapacity = 600;

    private readonly ConcurrentDictionary<string, ITelemetrySeries> _series = new(StringComparer.OrdinalIgnoreCase);

    public TelemetrySeries<T> GetOrCreate<T>(string mac) where T : struct, IEquatable<T>
    {
        ITelemetrySeries series = _series.GetOrAdd(mac, _ => new TelemetrySeries<T>(SeriesCapacity));

        if (series is TelemetrySeries<T> typed)
        {
            return typed;
        }

        throw new TelemetryTypeMismatchException(mac, series.ValueKind, TelemetryValueConverter.KindOf<T>());
    }

    /// <summary>
    /// Stores a packet that arrived through the queue behind the non-generic interface. Pattern matching on the
    /// closed generic type recovers the typed packet, so the value is never boxed.
    /// </summary>
    public void Append(ITelemetryPacket packet)
    {
        switch (packet)
        {
            case TelemetryPacket<float> f:
                GetOrCreate<float>(f.DeviceMac).Add(f);
                break;
            case TelemetryPacket<int> i:
                GetOrCreate<int>(i.DeviceMac).Add(i);
                break;
            case TelemetryPacket<bool> b:
                GetOrCreate<bool>(b.DeviceMac).Add(b);
                break;
            default:
                throw new NotSupportedException($"Unsupported packet type {packet.GetType().Name}.");
        }
    }

    public bool TryGet(string mac, [NotNullWhen(true)] out ITelemetrySeries? series) =>
        _series.TryGetValue(mac, out series);

    public double[] RecentValues(string mac, int max) =>
        TryGet(mac, out ITelemetrySeries? series) ? series.GetRecentValues(max) : Array.Empty<double>();
}
