using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SmartX.Core.Contracts;

namespace SmartX.Api.Services;

public sealed class SimulatorOptions
{
    public bool Enabled { get; set; } = true;
    public bool AutoEvents { get; set; } = true;
    public int AutoEventMinSeconds { get; set; } = 60;
    public int AutoEventMaxSeconds { get; set; } = 90;
    public int BackfillBatches { get; set; } = 10;
    public int BackfillReadingsPerBatch { get; set; } = 30;
}

/// <summary>Shared switches that let the dashboard trigger spikes, disconnects and outages on demand.</summary>
public sealed class SimulatorControl
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _outages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _disconnects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _pendingSpikes = new(StringComparer.OrdinalIgnoreCase);
    private long _generated;
    private volatile bool _enabled;

    public SimulatorControl(IOptions<SimulatorOptions> options, TimeProvider time)
    {
        _enabled = options.Value.Enabled;
        StartedAt = time.GetUtcNow();
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public DateTimeOffset StartedAt { get; }

    public void StartOutage(string subZoneId, TimeSpan duration, DateTimeOffset now) => _outages[subZoneId] = now + duration;

    public void Disconnect(string mac, TimeSpan duration, DateTimeOffset now) => _disconnects[mac] = now + duration;

    public void QueueSpike(string mac, int readings) =>
        _pendingSpikes.AddOrUpdate(mac, readings, (_, existing) => Math.Max(existing, readings));

    public bool IsSilenced(SensorDevice device, DateTimeOffset now) =>
        IsSilencedMac(device.MacAddress, device.Location.SubZoneId, now);

    /// <summary>True while the device is cut off by a simulated outage or disconnect.</summary>
    public bool IsSilencedMac(string mac, string subZoneId, DateTimeOffset now)
    {
        if (_outages.TryGetValue(subZoneId, out DateTimeOffset outageUntil) && outageUntil > now)
        {
            return true;
        }

        return _disconnects.TryGetValue(mac, out DateTimeOffset disconnectUntil) && disconnectUntil > now;
    }

    /// <summary>Returns true (and counts down) while a device still has spike readings queued.</summary>
    public bool TryConsumeSpike(string mac)
    {
        while (_pendingSpikes.TryGetValue(mac, out int remaining))
        {
            if (remaining <= 0)
            {
                _pendingSpikes.TryRemove(mac, out _);
                return false;
            }

            if (_pendingSpikes.TryUpdate(mac, remaining - 1, remaining))
            {
                return true;
            }
        }

        return false;
    }

    public void CountGenerated() => Interlocked.Increment(ref _generated);

    public SimulatorStatusDto Status(int simulatedDevices, DateTimeOffset now) => new(
        Enabled,
        simulatedDevices,
        Interlocked.Read(ref _generated),
        _outages.Where(kv => kv.Value > now).Select(kv => $"{kv.Key} until {kv.Value.ToUniversalTime():HH:mm:ss} UTC").ToArray(),
        _disconnects.Where(kv => kv.Value > now).Select(kv => $"{kv.Key} until {kv.Value.ToUniversalTime():HH:mm:ss} UTC").ToArray(),
        StartedAt);
}
