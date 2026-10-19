using SmartX.Core.Contracts;
using SmartX.Core.Devices;
using SmartX.Core.Readings;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>Uses the overloaded operators on <see cref="PowerMeterReading"/> to aggregate and compare load.</summary>
public sealed class MeterAnalyticsService
{
    private readonly DeviceRegistry _registry;
    private readonly PulseMonitor _pulse;
    private readonly TopologyService _topology;

    public MeterAnalyticsService(DeviceRegistry registry, PulseMonitor pulse, TopologyService topology)
    {
        _registry = registry;
        _pulse = pulse;
        _topology = topology;
    }

    public List<(SensorDevice Device, PowerMeterReading Reading)> CurrentReadings()
    {
        var readings = new List<(SensorDevice Device, PowerMeterReading Reading)>();

        foreach (SensorDevice device in _registry.All())
        {
            if (device.Category != SensorCategory.PowerConsumption)
            {
                continue;
            }

            if (_pulse.TryGetLatest(device.MacAddress, out double watts, out DateTimeOffset lastSeen))
            {
                readings.Add((device, new PowerMeterReading(device.MacAddress, watts, lastSeen)));
            }
        }

        return readings;
    }

    public PowerMeterDto[] ListMeters() =>
        CurrentReadings().Select(r => ToDto(r.Reading, r.Device.DisplayName)).ToArray();

    public MeterAggregationDto? Aggregate(string[]? ids, double toleranceWatts)
    {
        List<(SensorDevice Device, PowerMeterReading Reading)> all = CurrentReadings();
        if (all.Count == 0)
        {
            return null;
        }

        var selected = new List<(SensorDevice Device, PowerMeterReading Reading)>();
        if (ids is null || ids.Length == 0)
        {
            selected.AddRange(all.Take(2));
        }
        else
        {
            foreach (string id in ids)
            {
                if (!MacAddress.TryNormalize(id, out string mac))
                {
                    continue;
                }

                foreach (var candidate in all)
                {
                    if (string.Equals(candidate.Device.MacAddress, mac, StringComparison.OrdinalIgnoreCase) &&
                        !selected.Any(s => s.Device.MacAddress == candidate.Device.MacAddress))
                    {
                        selected.Add(candidate);
                    }
                }
            }
        }

        if (selected.Count == 0)
        {
            return null;
        }

        // Meter3 = Meter1 + Meter2 (+ ...)
        PowerMeterReading aggregate = selected[0].Reading;
        for (int i = 1; i < selected.Count; i++)
        {
            aggregate += selected[i].Reading;
        }

        string aggregateName = string.Join(" + ", selected.Select(s => s.Device.DisplayName));
        PowerMeterDto? deltaDto = null;
        string comparison = "Select two meters to compare their load.";
        bool within = false;

        if (selected.Count >= 2)
        {
            PowerMeterReading first = selected[0].Reading;
            PowerMeterReading second = selected[1].Reading;
            string firstName = selected[0].Device.DisplayName;
            string secondName = selected[1].Device.DisplayName;

            PowerMeterReading delta = first - second;
            deltaDto = ToDto(delta, $"{firstName} − {secondName}");
            within = first.IsWithin(second, toleranceWatts);

            if (first > second)
            {
                comparison = $"{firstName} is drawing {delta.Watts:N0} W more than {secondName}.";
            }
            else if (first < second)
            {
                comparison = $"{firstName} is drawing {Math.Abs(delta.Watts):N0} W less than {secondName}.";
            }
            else
            {
                comparison = $"{firstName} and {secondName} are drawing exactly the same load.";
            }
        }

        var subZoneTotals = new List<PowerMeterDto>();
        foreach (var group in all.GroupBy(r => r.Device.Location.SubZoneId, StringComparer.OrdinalIgnoreCase))
        {
            string label = _topology.TryGetSubZone(group.Key, out GridGroupDto? subZone) ? subZone.SubZoneName : group.Key;
            PowerMeterReading total = PowerMeterReading.Total(group.Select(g => g.Reading).ToList(), group.Key);
            subZoneTotals.Add(ToDto(total, label));
        }

        return new MeterAggregationDto(
            selected.Select(s => ToDto(s.Reading, s.Device.DisplayName)).ToArray(),
            ToDto(aggregate, aggregateName),
            deltaDto,
            comparison,
            within,
            toleranceWatts,
            subZoneTotals.ToArray());
    }

    private static PowerMeterDto ToDto(PowerMeterReading reading, string name) =>
        new(reading.MeterId, name, Math.Round(reading.Watts, 1), reading.Timestamp, reading.SourceCount);
}
