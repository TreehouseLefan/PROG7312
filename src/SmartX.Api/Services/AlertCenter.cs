using Microsoft.Extensions.Options;
using SmartX.Core.Contracts;
using SmartX.Core.Devices;
using SmartX.Core.Pulse;
using SmartX.Core.Streaming;

namespace SmartX.Api.Services;

/// <summary>Thresholds for critical telemetry. Bound from the "Alerts" section of appsettings.json.</summary>
public sealed class AlertOptions
{
    public double MoistureCriticalBelow { get; set; } = 20;
    public double MoistureClearAbove { get; set; } = 22;
    public double PowerCriticalAbove { get; set; } = 4000;
    public double PowerClearBelow { get; set; } = 3500;
    public int OutOfRangeExpirySeconds { get; set; } = 120;
}

/// <summary>Identity of an alert: one per device per alert type.</summary>
public readonly record struct AlertKey(MacKey Mac, AlertType Type);

/// <summary>
/// Unique alert tracking.
/// <list type="bullet">
/// <item><c>HashSet&lt;AlertKey&gt;</c> of active alerts: <c>Add</c> returns false for an alert that is already
/// active, so a sensor that keeps reporting the same fault produces one alert, not hundreds.</item>
/// <item><c>HashSet&lt;MacKey&gt;</c> of disconnected nodes: the Pulse engine reports every offline device every second
/// (level-triggered); the set turns that into exactly one disconnect alert per outage.</item>
/// </list>
/// Absorbed duplicates are counted so the dashboard can show how much noise was suppressed.
/// </summary>
public sealed class AlertCenter
{
    private readonly object _gate = new();
    private readonly HashSet<AlertKey> _active = new();
    private readonly HashSet<MacKey> _disconnected = new();
    private readonly Dictionary<AlertKey, AlertState> _details = new();
    private readonly Dictionary<MacKey, Queue<DateTimeOffset>> _disconnectTimes = new();
    private readonly AlertOptions _options;
    private readonly TimelineService _timeline;
    private long _raised;
    private long _duplicates;
    private long _cleared;
    private long _version;

    public AlertCenter(IOptions<AlertOptions> options, TimelineService timeline)
    {
        _options = options.Value;
        _timeline = timeline;
    }

    /// <summary>Raised once per new alert (never for duplicates), outside the lock.</summary>
    public event Action<AlertDto>? AlertRaised;

    public AlertOptions Options => _options;

    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Decides at the gateway door whether a reading is critical and must take the priority lane.
    /// Returns null for normal readings.
    /// </summary>
    public (AlertSeverity Severity, string Reason)? ClassifyCritical(SensorDevice device, double value)
    {
        switch (device.Metric)
        {
            case "soil_moisture" when value < _options.MoistureCriticalBelow:
                return (AlertSeverity.Critical, $"Critical moisture drop: {value:0.#}% < {_options.MoistureCriticalBelow:0}%");
            case "power_draw" when value >= _options.PowerCriticalAbove:
                return (AlertSeverity.Critical, $"Severe power spike: {value:N0} W ≥ {_options.PowerCriticalAbove:N0} W");
            default:
                return null;
        }
    }

    /// <summary>Raises or clears threshold alerts after a reading has been processed.</summary>
    public void EvaluateReading(SensorDevice device, double value, DateTimeOffset at)
    {
        if (device.Metric == "soil_moisture")
        {
            if (value < _options.MoistureCriticalBelow)
            {
                Raise(device, AlertType.CriticalMoisture, AlertSeverity.Critical, $"{device.DisplayName} soil moisture at {value:0.#}% (below {_options.MoistureCriticalBelow:0}%)", at);
            }
            else if (value >= _options.MoistureClearAbove)
            {
                Clear(device, AlertType.CriticalMoisture, at);
            }
        }
        else if (device.Metric == "power_draw")
        {
            if (value >= _options.PowerCriticalAbove)
            {
                Raise(device, AlertType.PowerSurge, AlertSeverity.Critical, $"{device.DisplayName} drawing {value:N0} W (limit {_options.PowerCriticalAbove:N0} W)", at);
            }
            else if (value < _options.PowerClearBelow)
            {
                Clear(device, AlertType.PowerSurge, at);
            }
        }
    }

    /// <summary>Returns true if a new alert was created, false if it was a duplicate of an active one.</summary>
    public bool Raise(SensorDevice device, AlertType type, AlertSeverity severity, string message, DateTimeOffset at)
    {
        var key = new AlertKey(device.Key, type);
        AlertDto created;

        lock (_gate)
        {
            if (!_active.Add(key))
            {
                // Already active: absorb the duplicate.
                _duplicates++;
                if (_details.TryGetValue(key, out AlertState? existing))
                {
                    existing.LastSeenAt = at;
                    existing.Duplicates++;
                    existing.Message = message;
                }

                return false;
            }

            var state = new AlertState(device, type, severity, message, at);
            _details[key] = state;
            _raised++;
            _version++;
            created = state.ToDto();
        }

        _timeline.AddHistory(HistoryKind.Alert, $"{type}: {device.DisplayName}", message, device.MacAddress, at);
        AlertRaised?.Invoke(created);
        return true;
    }

    public bool Clear(SensorDevice device, AlertType type, DateTimeOffset at)
    {
        var key = new AlertKey(device.Key, type);
        lock (_gate)
        {
            if (!_active.Remove(key))
            {
                return false;
            }

            _details.Remove(key);
            _cleared++;
            _version++;
        }

        _timeline.AddHistory(HistoryKind.AlertCleared, $"{type} cleared: {device.DisplayName}", "Back within limits.", device.MacAddress, at);
        return true;
    }

    /// <summary>Level-triggered disconnect report; only the first report per outage raises an alert.</summary>
    public void ReportDisconnected(SensorDevice device, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_disconnected.Add(device.Key))
            {
                _duplicates++;
                if (_details.TryGetValue(new AlertKey(device.Key, AlertType.Disconnected), out AlertState? existing))
                {
                    existing.LastSeenAt = at;
                    existing.Duplicates++;
                }

                return;
            }

            if (!_disconnectTimes.TryGetValue(device.Key, out Queue<DateTimeOffset>? times))
            {
                times = new Queue<DateTimeOffset>();
                _disconnectTimes[device.Key] = times;
            }

            times.Enqueue(at);
            while (times.Count > 20)
            {
                times.Dequeue();
            }
        }

        Raise(device, AlertType.Disconnected, AlertSeverity.High, $"{device.DisplayName} stopped reporting (heartbeat suspicion over threshold)", at);
    }

    public void ReportReconnected(SensorDevice device, DateTimeOffset at)
    {
        bool removed;
        lock (_gate)
        {
            removed = _disconnected.Remove(device.Key);
        }

        if (removed)
        {
            Clear(device, AlertType.Disconnected, at);
        }
    }

    /// <summary>Called by the Pulse engine whenever a tile changes state.</summary>
    public void OnPulseTransition(SensorDevice device, PulseState from, PulseState to, DateTimeOffset at)
    {
        AlertType anomaly = device.IsActuator ? AlertType.Chatter : AlertType.Spike;

        if (to == PulseState.Spike)
        {
            string message = device.IsActuator
                ? $"{device.DisplayName} is toggling unusually often (chatter)"
                : $"{device.DisplayName} broke its rolling baseline (|z| above limit for consecutive readings)";
            Raise(device, anomaly, AlertSeverity.Elevated, message, at);
        }
        else if (from == PulseState.Spike)
        {
            Clear(device, anomaly, at);
        }

        if (to == PulseState.Offline)
        {
            ReportDisconnected(device, at);
        }
        else if (to is PulseState.Healthy or PulseState.Spike)
        {
            ReportReconnected(device, at);
        }
    }

    /// <summary>Re-reports every device that is currently offline. Duplicates are absorbed by the HashSet.</summary>
    public void SyncOffline(IEnumerable<SensorDevice> offlineDevices, DateTimeOffset at)
    {
        foreach (SensorDevice device in offlineDevices)
        {
            ReportDisconnected(device, at);
        }
    }

    /// <summary>Expires out-of-range alerts once a device has behaved for a while.</summary>
    public void Sweep(DateTimeOffset now, Func<MacKey, SensorDevice?> resolve)
    {
        var expired = new List<AlertKey>();
        lock (_gate)
        {
            foreach ((AlertKey key, AlertState state) in _details)
            {
                if (key.Type == AlertType.OutOfRange &&
                    (now - state.LastSeenAt).TotalSeconds > _options.OutOfRangeExpirySeconds)
                {
                    expired.Add(key);
                }
            }
        }

        foreach (AlertKey key in expired)
        {
            SensorDevice? device = resolve(key.Mac);
            if (device is not null)
            {
                Clear(device, key.Type, now);
            }
        }
    }

    public bool IsActive(MacKey mac, AlertType type)
    {
        lock (_gate)
        {
            return _active.Contains(new AlertKey(mac, type));
        }
    }

    public bool IsDisconnected(MacKey mac)
    {
        lock (_gate)
        {
            return _disconnected.Contains(mac);
        }
    }

    public AlertType[] ActiveFor(MacKey mac)
    {
        lock (_gate)
        {
            var types = new List<AlertType>();
            foreach (AlertType type in Enum.GetValues<AlertType>())
            {
                if (_active.Contains(new AlertKey(mac, type)))
                {
                    types.Add(type);
                }
            }

            return types.ToArray();
        }
    }

    public AlertDto[] Active()
    {
        lock (_gate)
        {
            return _details.Values
                .OrderBy(s => s.Severity)
                .ThenByDescending(s => s.RaisedAt)
                .Select(s => s.ToDto())
                .ToArray();
        }
    }

    /// <summary>Number of separate disconnects this device had inside the window (for flapping detection).</summary>
    public int RecentDisconnects(MacKey mac, DateTimeOffset since)
    {
        lock (_gate)
        {
            if (!_disconnectTimes.TryGetValue(mac, out Queue<DateTimeOffset>? times))
            {
                return 0;
            }

            int count = 0;
            foreach (DateTimeOffset time in times)
            {
                if (time >= since)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public AlertBoardDto Board()
    {
        lock (_gate)
        {
            AlertDto[] active = _details.Values
                .OrderBy(s => s.Severity)
                .ThenByDescending(s => s.RaisedAt)
                .Select(s => s.ToDto())
                .ToArray();

            string[] disconnected = _disconnected.Select(m => m.ToString()).OrderBy(m => m, StringComparer.Ordinal).ToArray();
            return new AlertBoardDto(active, disconnected, _raised, _duplicates, _cleared);
        }
    }

    private sealed class AlertState
    {
        public AlertState(SensorDevice device, AlertType type, AlertSeverity severity, string message, DateTimeOffset at)
        {
            Device = device;
            Type = type;
            Severity = severity;
            Message = message;
            RaisedAt = at;
            LastSeenAt = at;
        }

        public SensorDevice Device { get; }
        public AlertType Type { get; }
        public AlertSeverity Severity { get; }
        public string Message { get; set; }
        public DateTimeOffset RaisedAt { get; }
        public DateTimeOffset LastSeenAt { get; set; }
        public long Duplicates { get; set; }

        public AlertDto ToDto() => new(
            $"{Device.MacAddress}|{Type}",
            Device.MacAddress,
            Device.DisplayName,
            Device.Location.SubZoneId,
            Type,
            Severity,
            Message,
            RaisedAt,
            LastSeenAt,
            Duplicates);
    }
}
