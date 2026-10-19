using SmartX.Api.Services;

namespace SmartX.Api.Simulation;

/// <summary>
/// A small physical model so commands have visible consequences:
/// soil in Sub Zones A and B slowly dries out "Pump A" irrigates Sub Zone B and "Pump B" irrigates
/// </summary>
public sealed class SimWorld
{
    private const double Evaporation = 0.02;   // % per second
    private const double DryOutRate = 0.12;    // % per second during a dryout event
    private const double PumpRate = 0.15;      // % per second while irrigating
    private const double MinLevel = 6;
    private const double MaxLevel = 72;

    private readonly object _gate = new();
    private readonly Dictionary<string, double> _levels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sz-a"] = 44,
        ["sz-b"] = 40
    };

    private readonly Dictionary<string, DateTimeOffset> _dryUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeviceRegistry _registry;
    private DateTimeOffset _last;
    private string? _pumpAMac;
    private string? _pumpBMac;
    private bool _pumpA;
    private bool _pumpB;

    public SimWorld(DeviceRegistry registry, TimeProvider time)
    {
        _registry = registry;
        _last = time.GetUtcNow();
    }

    public bool PumpAOn
    {
        get
        {
            lock (_gate)
            {
                return _pumpA;
            }
        }
    }

    public bool PumpBOn
    {
        get
        {
            lock (_gate)
            {
                return _pumpB;
            }
        }
    }

    /// <summary>Moves the model forward using the actuator states the devices currently hold.</summary>
    public void Advance(DateTimeOffset now, IReadOnlyDictionary<string, bool> actuatorStates)
    {
        ResolvePumps();

        lock (_gate)
        {
            double dt = Math.Clamp((now - _last).TotalSeconds, 0, 5);
            _last = now;

            _pumpA = _pumpAMac is not null && actuatorStates.TryGetValue(_pumpAMac, out bool a) && a;
            _pumpB = _pumpBMac is not null && actuatorStates.TryGetValue(_pumpBMac, out bool b) && b;

            bool anyValveOpen = false;
            bool anyValveKnown = false;
            foreach (SensorDevice valve in _registry.All().Where(d => d.Metric == "valve_state"))
            {
                if (actuatorStates.TryGetValue(valve.MacAddress, out bool open))
                {
                    anyValveKnown = true;
                    anyValveOpen |= open;
                }
            }

            bool water = !anyValveKnown || anyValveOpen;

            foreach (string zone in _levels.Keys.ToList())
            {
                bool drying = _dryUntil.TryGetValue(zone, out DateTimeOffset until) && until > now;
                double rate = drying ? -DryOutRate : -Evaporation;
                bool pump = zone.Equals("sz-b", StringComparison.OrdinalIgnoreCase) ? _pumpA : _pumpB;
                if (pump && water)
                {
                    // Irrigation slows as the soil approaches saturation (equilibrium ≈ 65 %).
                    rate += PumpRate * Math.Max(0, 1 - (_levels[zone] / 75));
                }

                double next = _levels[zone] + (rate * dt);
                _levels[zone] = Math.Clamp(next, MinLevel, MaxLevel);
            }
        }
    }

    /// <summary>Forces a fast dry out so a critical moisture alert happens within about half a minute.</summary>
    public void DryOut(string subZoneId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_levels.ContainsKey(subZoneId))
            {
                throw new ArgumentException($"Sub-Zone '{subZoneId}' has no soil model (use sz-a or sz-b).", nameof(subZoneId));
            }

            _levels[subZoneId] = Math.Min(_levels[subZoneId], 23);
            _dryUntil[subZoneId] = now.AddSeconds(90);
        }
    }

    public bool HasSoilModel(string subZoneId)
    {
        lock (_gate)
        {
            return _levels.ContainsKey(subZoneId);
        }
    }

    public double SoilMoisture(SensorDevice device, bool spike)
    {
        double level;
        lock (_gate)
        {
            level = _levels.TryGetValue(device.Location.SubZoneId, out double l) ? l : 42;
        }

        int lastByte = Convert.ToInt32(device.MacAddress[^2..], 16);
        double offset = ((lastByte % 5) - 2) * 0.6;
        double wave = 0.4 * Math.Sin(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 60_000d + lastByte);
        double value = level + offset + wave + (SignalModels.Gaussian() * 0.35);
        if (spike)
        {
            value += 35;
        }

        return Math.Clamp(value, 0, 100);
    }

    /// <summary>Power for the two pump meters follows their pump; returns null for other meters.</summary>
    public double? PumpLoad(SensorDevice meter, bool spike)
    {
        bool? running = null;
        if (meter.DisplayName.Contains("Pump A", StringComparison.OrdinalIgnoreCase))
        {
            running = PumpAOn;
        }
        else if (meter.DisplayName.Contains("Pump B", StringComparison.OrdinalIgnoreCase))
        {
            running = PumpBOn;
        }

        if (running is not bool on)
        {
            return null;
        }

        double value = (on ? 1150 : 180) + (SignalModels.Gaussian() * (on ? 35 : 8));
        if (spike)
        {
            value += 4200;
        }

        return Math.Max(0, value);
    }

    private void ResolvePumps()
    {
        if (_pumpAMac is not null && _pumpBMac is not null)
        {
            return;
        }

        foreach (SensorDevice device in _registry.All())
        {
            if (device.Metric != "pump_relay")
            {
                continue;
            }

            if (device.DisplayName.Contains("Pump A", StringComparison.OrdinalIgnoreCase))
            {
                _pumpAMac = device.MacAddress;
            }
            else if (device.DisplayName.Contains("Pump B", StringComparison.OrdinalIgnoreCase))
            {
                _pumpBMac = device.MacAddress;
            }
        }
    }
}
