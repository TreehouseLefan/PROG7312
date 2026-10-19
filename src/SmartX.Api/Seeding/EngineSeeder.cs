using SmartX.Api.Services;
using SmartX.Core.Contracts;

namespace SmartX.Api.Seeding;

/// <summary>
/// Seeds the Action Engine with a week of simulated operator sessions so it has patterns to learn from on
/// first start (the brief asks for a heavily seeded simulation). The sessions go through the same
    /// <see cref="ActionEngine.LearnHistory"/> path as live behaviour and use the same context / action keys,
        /// but they are not counted in the live hit-rate statistics. Every pattern is plausible operator behaviour:
    /// <list type="bullet">
/// <item>Sub Zone B moisture drops below 20% → search "pump a" → turn on Pump A (the brief's example).</item>
/// <item>Sub Zone A moisture drops below 20% → turn on Pump B.</item>
/// <item>A severe power spike on the Pump A meter → inspect the meter → turn Pump A off.</item>
/// <item>A Sub Zone D smart switch chatters → turn that switch off.</item>
/// <item>Some situations are ignored, so confidence is realistic rather than 100%.</item>
/// </list>
/// </summary>
public static class EngineSeeder
{
    public static int Seed(IServiceProvider services)
    {
        var registry = services.GetRequiredService<DeviceRegistry>();
        var engine = services.GetRequiredService<ActionEngine>();
        var time = services.GetRequiredService<TimeProvider>();
        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SmartX.EngineSeeder");

        IReadOnlyList<SensorDevice> devices = registry.All();
        SensorDevice? pumpA = devices.FirstOrDefault(d => d.DisplayName.Contains("Pump A Relay", StringComparison.Ordinal));
        SensorDevice? pumpB = devices.FirstOrDefault(d => d.DisplayName.Contains("Pump B Relay", StringComparison.Ordinal));
        SensorDevice? meterA = devices.FirstOrDefault(d => d.DisplayName.Contains("Pump A Meter", StringComparison.Ordinal));
        SensorDevice? switchD = devices.FirstOrDefault(d => d.Metric == "switch_state");
        List<SensorDevice> moistureB = devices.Where(d => d.Metric == "soil_moisture" && d.Location.SubZoneId == "sz-b").ToList();
        List<SensorDevice> moistureA = devices.Where(d => d.Metric == "soil_moisture" && d.Location.SubZoneId == "sz-a").ToList();

        if (pumpA is null || pumpB is null || meterA is null || switchD is null || moistureA.Count == 0 || moistureB.Count == 0)
        {
            logger.LogWarning("Engine history not seeded: expected seed devices are missing.");
            return 0;
        }

        var random = new Random(2026);
        var events = new List<(DateTimeOffset At, bool IsAction, string Key)>();
        DateTimeOffset start = time.GetUtcNow().AddDays(-7);
        DateTimeOffset cursor = start;

        void Context(AlertType type, SensorDevice device, DateTimeOffset at)
        {
            events.Add((at, false, $"{type}@{device.Location.SubZoneId}"));
            events.Add((at, false, $"{type}@{device.MacAddress}"));
        }

        void Act(string key, DateTimeOffset at) => events.Add((at, true, key));

        string TurnOn(SensorDevice d) => $"Cmd:{CommandAction.TurnOn}:{d.MacAddress}";
        string TurnOff(SensorDevice d) => $"Cmd:{CommandAction.TurnOff}:{d.MacAddress}";

        // 40 sessions spread across the week, a few hours apart.
        for (int session = 0; session < 40; session++)
        {
            cursor = cursor.AddMinutes(150 + random.Next(0, 120));
            DateTimeOffset t = cursor;
            int scenario = session % 8;

            switch (scenario)
            {
                case 0:
                case 1:
                case 2:
                {
                    // Sub-Zone B dries out → search "pump a" → start Pump A (ignored about one time in seven).
                    Context(AlertType.CriticalMoisture, moistureB[random.Next(moistureB.Count)], t);
                    if (random.NextDouble() < 0.14)
                    {
                        break;
                    }

                    Act("Search:pump a", t.AddSeconds(30 + random.Next(60)));
                    Act(TurnOn(pumpA), t.AddSeconds(100 + random.Next(80)));
                    break;
                }

                case 3:
                case 4:
                {
                    // Sub-Zone A dries out → start Pump B.
                    Context(AlertType.CriticalMoisture, moistureA[random.Next(moistureA.Count)], t);
                    if (random.NextDouble() < 0.2)
                    {
                        break;
                    }

                    Act($"Inspect:{moistureA[random.Next(moistureA.Count)].MacAddress}", t.AddSeconds(25 + random.Next(40)));
                    Act(TurnOn(pumpB), t.AddSeconds(90 + random.Next(60)));
                    break;
                }

                case 5:
                {
                    // Power spike on Pump A's meter → inspect → stop Pump A.
                    Context(AlertType.PowerSurge, meterA, t);
                    Act($"Inspect:{meterA.MacAddress}", t.AddSeconds(20 + random.Next(30)));
                    if (random.NextDouble() < 0.75)
                    {
                        Act(TurnOff(pumpA), t.AddSeconds(70 + random.Next(60)));
                    }

                    break;
                }

                case 6:
                {
                    // A chattering smart switch is switched off.
                    Context(AlertType.Chatter, switchD, t);
                    Act(TurnOff(switchD), t.AddSeconds(40 + random.Next(60)));
                    break;
                }

                default:
                {
                    // Routine check with no alert: adds realistic background actions (lowers lift of generic actions).
                    Act($"Inspect:{devices[random.Next(devices.Count)].MacAddress}", t);
                    break;
                }
            }
        }

        engine.LearnHistory(events);
        logger.LogInformation("Seeded the Action Engine with {Events} historical operator events over 7 days.", events.Count);
        return events.Count;
    }
}
