using Microsoft.Extensions.Options;
using SmartX.Api.Services;
using SmartX.Api.Simulation;
using SmartX.Core.Contracts;
using SmartX.Core.Telemetry;
using SmartX.Core.Topology;

namespace SmartX.Api.Seeding;

/// <summary>
    /// Heavily seeds the gateway: a realistic South African deployment tree, 32 devices and roughly
/// ten thousand historical readings uploaded through jaggedarray batches.
/// </summary>
public static class DataSeeder
{
    private sealed record DeviceSpec(
        string SubZoneId,
        string Name,
        SensorCategory Category,
        string Metric,
        string Unit,
        int IntervalSeconds,
        double? Min,
        double? Max,
        string Room);

    public static void Seed(IServiceProvider services)
    {
        var topology = services.GetRequiredService<TopologyService>();
        var registration = services.GetRequiredService<DeviceRegistrationService>();
        var ingestion = services.GetRequiredService<TelemetryIngestionService>();
        var time = services.GetRequiredService<TimeProvider>();
        var options = services.GetRequiredService<IOptions<SimulatorOptions>>().Value;
        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SmartX.Seeder");

        topology.Seed(BuildTopology());

        int registered = 0;
        long historical = 0;
        List<DeviceSpec> specs = BuildDeviceSpecs();

        for (int i = 0; i < specs.Count; i++)
        {
            DeviceSpec spec = specs[i];
            var request = new RegisterDeviceRequest
            {
                MacAddress = $"24:0A:C4:{SubZoneByte(spec.SubZoneId):X2}:{i / 256:X2}:{i % 256:X2}",
                DisplayName = spec.Name,
                Category = spec.Category,
                SubZoneId = spec.SubZoneId,
                Room = spec.Room,
                Metric = spec.Metric,
                Unit = spec.Unit,
                ExpectedIntervalSeconds = spec.IntervalSeconds,
                MinValue = spec.Min,
                MaxValue = spec.Max,
                SimulateTelemetry = true
            };

            GatewayOutcome<SensorDevice> outcome = registration.Register(request);
            if (outcome.Value is null)
            {
                string reason = outcome.Errors is null
                    ? outcome.Message ?? "unknown error"
                    : string.Join("; ", outcome.Errors.SelectMany(e => e.Value));
                logger.LogWarning("Seed device {Name} was rejected: {Reason}", spec.Name, reason);
                continue;
            }

            registered++;
            historical += Backfill(outcome.Value, ingestion, time.GetUtcNow(), options);
        }

        // Initial actuator states: Pump B is running for Sub-Zone A; Pump A is off, so Sub Zone B slowly dries.
         var commands = services.GetRequiredService<CommandService>();
        foreach (SensorDevice device in services.GetRequiredService<DeviceRegistry>().All())
        {
            if (device.DisplayName.Contains("Pump B Relay", StringComparison.Ordinal))
            {
                commands.SetInitialState(device.Key, true);
            }
        }

        logger.LogInformation(
            "Seeded {Devices} devices and {Readings} historical readings across {SubZones} Sub-Zones.",
            registered,
            historical,
            topology.GetGroups().Length);
    }

    private static long Backfill(SensorDevice device, TelemetryIngestionService ingestion, DateTimeOffset now, SimulatorOptions options)
    {
        int batchCount = Math.Clamp(options.BackfillBatches, 1, 100);
        int perBatch = Math.Clamp(options.BackfillReadingsPerBatch, 3, 1000);

        // Jagged: batch lengths vary (perBatch - 1, perBatch, perBatch + 1) like real reconnect uploads.
        var lengths = new int[batchCount];
        int total = 0;
        for (int b = 0; b < batchCount; b++)
        {
            lengths[b] = perBatch + (b % 3) - 1;
            total += lengths[b];
        }

        TimeSpan interval = TimeSpan.FromSeconds(device.ExpectedIntervalSeconds);
        DateTimeOffset first = now - (interval * total);
        SignalModel model = SignalModels.For(device.Metric);

        // Pump meters follow their pump: Pump A starts off (idle draw), Pump B starts on.
        if (device.DisplayName.Contains("Pump A Meter", StringComparison.Ordinal))
        {
            model = new SignalModel(180, 0, 8, 600, 0);
        }
        else if (device.DisplayName.Contains("Pump B Meter", StringComparison.Ordinal))
        {
            model = new SignalModel(1150, 0, 35, 600, 0);
        }
        TelemetryBatchResult result;

        switch (device.ValueKind)
        {
            case TelemetryValueKind.Float:
            {
                var batches = new float[batchCount][];
                int index = 0;
                for (int b = 0; b < batchCount; b++)
                {
                    batches[b] = new float[lengths[b]];
                    for (int i = 0; i < lengths[b]; i++, index++)
                    {
                        DateTimeOffset at = first + (interval * index);
                        batches[b][i] = (float)Clamp(device, SignalModels.Sample(model, device.MacAddress, at, false));
                    }
                }

                result = ingestion.IngestHistorical(device, batches, first, interval);
                break;
            }

            case TelemetryValueKind.Int:
            {
                var batches = new int[batchCount][];
                int index = 0;
                for (int b = 0; b < batchCount; b++)
                {
                    batches[b] = new int[lengths[b]];
                    for (int i = 0; i < lengths[b]; i++, index++)
                    {
                        DateTimeOffset at = first + (interval * index);
                        batches[b][i] = (int)Math.Round(Clamp(device, SignalModels.Sample(model, device.MacAddress, at, false)));
                    }
                }

                result = ingestion.IngestHistorical(device, batches, first, interval);
                break;
            }

            default:
            {
                var batches = new bool[batchCount][];
                int index = 0;
                bool state = false;
                for (int b = 0; b < batchCount; b++)
                {
                    batches[b] = new bool[lengths[b]];
                    for (int i = 0; i < lengths[b]; i++, index++)
                    {
                        if (index % 40 == 39)
                        {
                            state = !state;
                        }

                        batches[b][i] = state;
                    }
                }

                result = ingestion.IngestHistorical(device, batches, first, interval);
                break;
            }
        }

        return result.ReadingsAccepted;
    }

    private static double Clamp(SensorDevice device, double value) =>
        Math.Clamp(value, device.MinValue ?? double.MinValue, device.MaxValue ?? double.MaxValue);

    private static int SubZoneByte(string subZoneId) => subZoneId switch
    {
        "sz-a" => 0x0A,
        "sz-b" => 0x0B,
        "sz-c" => 0x0C,
        "sz-d" => 0x0D,
        _ => 0x0F
    };

    public static List<DeploymentNode> BuildTopology() => new()
    {
        new DeploymentNode
        {
            Id = "fac-a",
            Name = "Facility A",
            Description = "Stellenbosch Hydroponic Farm",
            Level = DeploymentLevel.Facility,
            Profile = new ConfigProfile { MaxSampleIntervalSeconds = 30, EncryptionRequired = true },
            Children =
            {
                new DeploymentNode
                {
                    Id = "zone-1",
                    Name = "Zone 1",
                    Description = "Greenhouse North",
                    Level = DeploymentLevel.Zone,
                    Profile = new ConfigProfile { MaxSampleIntervalSeconds = 10 },
                    Children =
                    {
                        new DeploymentNode { Id = "sz-a", Name = "Sub-Zone A", Description = "Lettuce Beds", Level = DeploymentLevel.SubZone },
                        new DeploymentNode { Id = "sz-b", Name = "Sub-Zone B", Description = "Tomato Vines", Level = DeploymentLevel.SubZone }
                    }
                },
                new DeploymentNode
                {
                    Id = "zone-2",
                    Name = "Zone 2",
                    Description = "Pump House",
                    Level = DeploymentLevel.Zone,
                    Profile = new ConfigProfile { MaxSampleIntervalSeconds = 15 },
                    Children =
                    {
                        new DeploymentNode { Id = "sz-c", Name = "Sub-Zone C", Description = "Irrigation Pumps", Level = DeploymentLevel.SubZone }
                    }
                }
            }
        },
        new DeploymentNode
        {
            Id = "fac-b",
            Name = "Facility B",
            Description = "Sandton Utility Tracker",
            Level = DeploymentLevel.Facility,
            Profile = new ConfigProfile { MaxSampleIntervalSeconds = 60, EncryptionRequired = true },
            Children =
            {
                new DeploymentNode
                {
                    Id = "zone-3",
                    Name = "Zone 3",
                    Description = "Residential Tower",
                    Level = DeploymentLevel.Zone,
                    Children =
                    {
                        new DeploymentNode { Id = "sz-d", Name = "Sub-Zone D", Description = "Distribution Board", Level = DeploymentLevel.SubZone }
                    }
                }
            }
        }
    };

    private static List<DeviceSpec> BuildDeviceSpecs()
    {
        var specs = new List<DeviceSpec>();

        foreach (string subZone in new[] { "sz-a", "sz-b" })
        {
            string letter = subZone == "sz-a" ? "A" : "B";
            for (int n = 1; n <= 4; n++)
            {
                specs.Add(new DeviceSpec(subZone, $"{letter}-0{n} Soil Moisture", SensorCategory.Environmental, "soil_moisture", "%", 2, 0, 100, $"Bed {n}"));
            }

            for (int n = 5; n <= 6; n++)
            {
                specs.Add(new DeviceSpec(subZone, $"{letter}-0{n} Air Temp", SensorCategory.Environmental, "air_temperature", "°C", 2, -10, 60, "Canopy"));
            }

            for (int n = 7; n <= 8; n++)
            {
                specs.Add(new DeviceSpec(subZone, $"{letter}-0{n} Humidity", SensorCategory.Environmental, "humidity", "%", 2, 0, 100, "Canopy"));
            }
        }

        specs.Add(new DeviceSpec("sz-c", "C-01 Pump A Meter", SensorCategory.PowerConsumption, "power_draw", "W", 3, 0, 15000, "Pump Room"));
        specs.Add(new DeviceSpec("sz-c", "C-02 Pump B Meter", SensorCategory.PowerConsumption, "power_draw", "W", 3, 0, 15000, "Pump Room"));
        for (int n = 3; n <= 4; n++)
        {
            specs.Add(new DeviceSpec("sz-c", $"C-0{n} Booster Meter", SensorCategory.PowerConsumption, "power_draw", "W", 3, 0, 15000, "Pump Room"));
        }

        for (int n = 5; n <= 8; n++)
        {
            specs.Add(new DeviceSpec("sz-c", $"C-0{n} Valve", SensorCategory.Actuator, "valve_state", string.Empty, 5, null, null, "Manifold"));
        }

        // Part 2: pump relays that operators switch from the Command Stream.
        // Pump A irrigates Sub Zone B (Tomato Vines); Pump B irrigates Sub Zone A (Lettuce Beds).
        specs.Add(new DeviceSpec("sz-c", "C-09 Pump A Relay", SensorCategory.Actuator, "pump_relay", string.Empty, 5, null, null, "Pump Room"));
        specs.Add(new DeviceSpec("sz-c", "C-10 Pump B Relay", SensorCategory.Actuator, "pump_relay", string.Empty, 5, null, null, "Pump Room"));

        for (int n = 1; n <= 6; n++)
        {
            specs.Add(new DeviceSpec("sz-d", $"D-0{n} Unit Meter", SensorCategory.PowerConsumption, "power_draw", "W", 3, 0, 15000, $"Floor {n}"));
        }

        for (int n = 7; n <= 8; n++)
        {
            specs.Add(new DeviceSpec("sz-d", $"D-0{n} Smart Switch", SensorCategory.Actuator, "switch_state", string.Empty, 5, null, null, "Lobby"));
        }

        return specs;
    }
}
