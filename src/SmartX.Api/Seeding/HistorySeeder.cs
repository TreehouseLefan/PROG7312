using SmartX.Api.Services;

namespace SmartX.Api.Seeding;

/// <summary>
/// Backfills 24 hours of one minute readings per device into the AVL history, ending where the
/// Part 2 seed data begins, so the Mesh page can answer questions such as "moisture between 14:00
/// and 16:00 yesterday" from the first run. Values follow a daily cycle (warm, dry afternoons;
/// cool, damp nights) with deterministic noise per device, so every run produces the same history.
/// </summary>
public static class HistorySeeder
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SouthAfrica = TimeSpan.FromHours(2); // SAST, for the daily cycle

    public static void Seed(IServiceProvider services)
    {
        var registry = services.GetRequiredService<DeviceRegistry>();
        var history = services.GetRequiredService<TelemetryHistoryService>();
        var time = services.GetRequiredService<TimeProvider>();
        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SmartX.HistorySeeder");

        DateTimeOffset now = time.GetUtcNow();
        long inserted = 0;

        foreach (SensorDevice device in registry.All())
        {
            DateTimeOffset end = history.TryGetOldest(device.Key, out DateTimeOffset oldest) ? oldest : now;
            DateTimeOffset start = end - Window;
            var random = new Random(StableSeed(device.MacAddress));

            for (DateTimeOffset at = start; at < end; at += Step)
            {
                history.Record(device.Key, at, Sample(device, at, random));
                inserted++;
            }
        }

        logger.LogInformation("History seeder: {Count:N0} readings inserted into per-device AVL trees.", inserted);
    }

    private static double Sample(SensorDevice device, DateTimeOffset at, Random random)
    {
        double localHour = (at.ToOffset(SouthAfrica).TimeOfDay.TotalHours);

        if (device.IsActuator)
        {
            // Irrigation relays: on 05:30–06:30 and 17:30–18:30, off otherwise.
            bool on = localHour is >= 5.5 and < 6.5 or >= 17.5 and < 18.5;
            return on ? 1 : 0;
        }

        double min = device.MinValue ?? 0;
        double max = device.MaxValue ?? 100;
        if (max <= min)
        {
            max = min + 1;
        }

        // Realistic daily band per metric (inside the device's valid range and clear of alert thresholds).
        string metric = device.Metric.ToLowerInvariant();
        (double low, double high) = metric switch
        {
            _ when metric.Contains("moisture") => (35.0, 60.0),
            _ when metric.Contains("humidity") => (45.0, 85.0),
            _ when metric.Contains("temperature") => (14.0, 31.0),
            _ when metric.Contains("power") => (600.0, 3200.0),
            _ => (min + (max - min) * 0.3, min + (max - min) * 0.7)
        };

        // Peak at 14:00 local, trough at 02:00. Moisture and humidity run opposite to temperature.
        double cycle = Math.Cos((localHour - 14) / 24 * 2 * Math.PI);
        if (metric.Contains("moisture") || metric.Contains("humidity"))
        {
            cycle = -cycle;
        }

        double mid = (low + high) / 2;
        double amplitude = (high - low) / 2;
        double noise = (random.NextDouble() - 0.5) * (high - low) * 0.08;
        double value = mid + amplitude * cycle + noise;
        return Math.Round(Math.Clamp(value, min, max), 2);
    }

    /// <summary>string.GetHashCode is randomised per process, so hash the MAC ourselves (FNV 1a).</summary>
    private static int StableSeed(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash = (hash ^ char.ToUpperInvariant(c)) * 16777619;
            }

            return (int)hash;
        }
    }
}
