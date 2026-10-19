using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SmartX.Core.Contracts;
using SmartX.Core.Pulse;
using SmartX.Core.Readings;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Owns one <see cref="DeviceHealthTracker"/> per device, turns state transitions into incidents,
/// and produces the batched tile changes that are pushed to dashboards.
/// </summary>
public sealed class PulseMonitor
{
    private const double ValueRefreshSeconds = 3;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly PulseOptions _options;
    private readonly IncidentBook _incidents;
    private readonly TopologyService _topology;
    private readonly AlertCenter _alerts;
    private readonly TimeProvider _time;

    public PulseMonitor(
        IOptions<PulseOptions> options,
        IncidentBook incidents,
        TopologyService topology,
        AlertCenter alerts,
        TimeProvider time)
    {
        _options = options.Value;
        _incidents = incidents;
        _topology = topology;
        _alerts = alerts;
        _time = time;
    }

    public PulseOptions Options => _options;

    public void Track(SensorDevice device) => _entries.GetOrAdd(device.MacAddress, _ => CreateEntry(device));

    public PulseState OnReading(SensorDevice device, double value, DateTimeOffset timestamp, bool evaluate = true)
    {
        Entry entry = _entries.GetOrAdd(device.MacAddress, _ => CreateEntry(device));

        lock (entry.Gate)
        {
            entry.Tracker.RecordReading(value, timestamp);
            if (!evaluate)
            {
                return entry.Tracker.State;
            }

            PulseState state = entry.Tracker.Evaluate(_time.GetUtcNow());
            ApplyTransition(entry, state);
            return state;
        }
    }

    /// <summary>Re-evaluates every device and returns (tiles that changed since the last call, all tiles).</summary>
    public (PulseTileDto[] Changes, PulseTileDto[] All) Collect()
    {
        DateTimeOffset now = _time.GetUtcNow();
        var changes = new List<PulseTileDto>();
        var all = new List<PulseTileDto>(_entries.Count);

        foreach (Entry entry in _entries.Values)
        {
            lock (entry.Gate)
            {
                PulseState state = entry.Tracker.Evaluate(now);
                ApplyTransition(entry, state);

                double phiBucket = state == PulseState.Fading ? Math.Round(entry.Tracker.Phi, 1) : 0;
                double value = entry.Tracker.LastValue;
                bool valueDue = !value.Equals(entry.LastPublishedValue) &&
                                (now - entry.LastValuePublishedAt).TotalSeconds >= ValueRefreshSeconds;

                PulseTileDto tile = ToTile(entry);
                all.Add(tile);

                if (entry.ForcePublish ||
                    state != entry.LastPublishedState ||
                    !phiBucket.Equals(entry.LastPublishedPhiBucket) ||
                    valueDue)
                {
                    entry.ForcePublish = false;
                    entry.LastPublishedState = state;
                    entry.LastPublishedPhiBucket = phiBucket;
                    entry.LastPublishedValue = value;
                    entry.LastValuePublishedAt = now;
                    changes.Add(tile);
                }
            }
        }

        return (changes.ToArray(), SortTiles(all));
    }

    public PulseTileDto[] GetTiles()
    {
        DateTimeOffset now = _time.GetUtcNow();
        var tiles = new List<PulseTileDto>(_entries.Count);

        foreach (Entry entry in _entries.Values)
        {
            lock (entry.Gate)
            {
                PulseState state = entry.Tracker.Evaluate(now);
                ApplyTransition(entry, state);
                tiles.Add(ToTile(entry));
            }
        }

        return SortTiles(tiles);
    }

    public bool TryGetTile(string mac, out PulseTileDto? tile)
    {
        tile = null;
        if (!_entries.TryGetValue(mac, out Entry? entry))
        {
            return false;
        }

        lock (entry.Gate)
        {
            tile = ToTile(entry);
        }

        return true;
    }

    public bool TryGetBaseline(string mac, out RunningStats stats)
    {
        stats = default;
        if (!_entries.TryGetValue(mac, out Entry? entry))
        {
            return false;
        }

        lock (entry.Gate)
        {
            stats = entry.Tracker.BaselineStats();
        }

        return true;
    }

    public bool TryGetLatest(string mac, out double value, out DateTimeOffset lastSeen)
    {
        value = 0;
        lastSeen = default;
        if (!_entries.TryGetValue(mac, out Entry? entry))
        {
            return false;
        }

        lock (entry.Gate)
        {
            if (entry.Tracker.LastSeen is not DateTimeOffset seen)
            {
                return false;
            }

            value = entry.Tracker.LastValue;
            lastSeen = seen;
            return true;
        }
    }

    /// <summary>Forces a device's tile into the next broadcast (for example after an acknowledgement).</summary>
    public void MarkDirty(string mac)
    {
        if (_entries.TryGetValue(mac, out Entry? entry))
        {
            lock (entry.Gate)
            {
                entry.ForcePublish = true;
            }
        }
    }

    /// <summary>Sub-Zone level interpretation: shared silence suggests a gateway/network fault.</summary>
    public ZoneInsightDto[] GetInsights(IReadOnlyList<PulseTileDto> tiles)
    {
        var insights = new List<ZoneInsightDto>();

        foreach (GridGroupDto group in _topology.GetGroups())
        {
            int total = 0, healthy = 0, spike = 0, fading = 0, offline = 0;

            foreach (PulseTileDto tile in tiles)
            {
                if (!string.Equals(tile.SubZoneId, group.SubZoneId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                total++;
                switch (tile.State)
                {
                    case PulseState.Healthy:
                        healthy++;
                        break;
                    case PulseState.Spike:
                        spike++;
                        break;
                    case PulseState.Fading:
                        fading++;
                        break;
                    case PulseState.Offline:
                        offline++;
                        break;
                }
            }

            if (total == 0)
            {
                continue;
            }

            int silent = fading + offline;
            string severity;
            string message;

            if (total >= 3 && silent * 10 >= total * 6)
            {
                severity = "Critical";
                message = $"{silent} of {total} devices went silent together: likely a shared gateway, power or network fault in this Sub-Zone.";
            }
            else if (total >= 3 && spike * 2 >= total)
            {
                severity = "Warning";
                message = $"{spike} of {total} devices are spiking together: check for a shared environmental cause or calibration drift.";
            }
            else if (silent > 0)
            {
                severity = "Warning";
                message = silent == 1
                    ? "One isolated device is silent: inspect that device's power and connectivity."
                    : $"{silent} devices are silent independently: inspect each device.";
            }
            else if (spike > 0)
            {
                severity = "Info";
                message = spike == 1
                    ? "One isolated spike: open the device to inspect its sparkline."
                    : $"{spike} independent spikes: review each device.";
            }
            else
            {
                severity = "Ok";
                message = "All devices are reporting within their baselines.";
            }

            insights.Add(new ZoneInsightDto(
                group.SubZoneId,
                $"{group.SubZoneName} ({group.ZoneName})",
                total,
                healthy,
                spike,
                fading,
                offline,
                severity,
                message));
        }

        return insights.ToArray();
    }

    private Entry CreateEntry(SensorDevice device) =>
        new(device, new DeviceHealthTracker(device.ValueKind, device.ExpectedIntervalSeconds, _options), _time.GetUtcNow());

    private void ApplyTransition(Entry entry, PulseState state)
    {
        if (state == entry.PreviousState)
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        PulseState from = entry.PreviousState;
        entry.PreviousState = state;
        entry.StateChangedAt = now;
        _alerts.OnPulseTransition(entry.Device, from, state, now);

        switch (state)
        {
            case PulseState.Spike:
                IncidentKind kind = entry.Device.ValueKind == TelemetryValueKind.Bool ? IncidentKind.Chatter : IncidentKind.Spike;
                _incidents.Raise(entry.Device, kind, now);
                break;
            case PulseState.Offline:
                _incidents.Raise(entry.Device, IncidentKind.Offline, now);
                break;
            case PulseState.Healthy:
                _incidents.ResolveAll(entry.Device.MacAddress, now);
                break;
        }
    }

    private PulseTileDto ToTile(Entry entry)
    {
        SensorDevice device = entry.Device;
        DeviceHealthTracker tracker = entry.Tracker;
        IncidentDto? incident = _incidents.GetOpenFor(device.MacAddress);

        return new PulseTileDto(
            device.MacAddress,
            device.DisplayName,
            device.Category,
            device.Metric,
            device.Unit,
            device.Location.FacilityId,
            device.Location.ZoneId,
            device.Location.SubZoneId,
            tracker.State,
            Math.Round(tracker.LastValue, 2),
            Math.Round(tracker.LastZScore, 2),
            tracker.Phi,
            tracker.LastSeen,
            entry.StateChangedAt,
            incident?.Id,
            incident?.Acknowledged ?? false);
    }

    private static PulseTileDto[] SortTiles(List<PulseTileDto> tiles) =>
        tiles
            .OrderBy(t => t.SubZoneId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private sealed class Entry
    {
        public Entry(SensorDevice device, DeviceHealthTracker tracker, DateTimeOffset createdAt)
        {
            Device = device;
            Tracker = tracker;
            StateChangedAt = createdAt;
        }

        public object Gate { get; } = new();
        public SensorDevice Device { get; }
        public DeviceHealthTracker Tracker { get; }
        public PulseState PreviousState { get; set; } = PulseState.Unknown;
        public DateTimeOffset StateChangedAt { get; set; }
        public PulseState LastPublishedState { get; set; } = PulseState.Unknown;
        public double LastPublishedPhiBucket { get; set; } = -1;
        public double LastPublishedValue { get; set; } = double.NaN;
        public DateTimeOffset LastValuePublishedAt { get; set; } = DateTimeOffset.MinValue;
        public bool ForcePublish { get; set; } = true;
    }
}
