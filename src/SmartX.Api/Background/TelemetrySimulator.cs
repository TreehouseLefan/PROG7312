using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using SmartX.Api.Services;
using SmartX.Api.Simulation;
using SmartX.Core.Contracts;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Background;

/// <summary>
/// A virtual fleet of ESP32 devices. Every reading is sent as a real HTTP POST to
/// <c>/api/telemetry</c>, exactly as a physical device would send it, so it passes through the same
/// validation gateway, queues and dispatcher. Actuators fetch their desired state from
/// <c>/api/commands/shadows</c> once per second (like polling a device shadow) and report it back in
/// their telemetry, which confirms operator commands.
/// </summary>
public sealed class TelemetrySimulator : BackgroundService
{
    private readonly DeviceRegistry _registry;
    private readonly SimulatorControl _control;
    private readonly SimWorld _world;
    private readonly SimulatorOptions _options;
    private readonly TimeProvider _time;
    private readonly IServer _server;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelemetrySimulator> _logger;
    private readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly Dictionary<string, DateTimeOffset> _nextDue = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _deviceStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _lastReported = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastShadowSync = DateTimeOffset.MinValue;
    private DateTimeOffset _nextAutoEvent;
    private int _consecutiveFailures;

    public TelemetrySimulator(
        DeviceRegistry registry,
        SimulatorControl control,
        SimWorld world,
        IOptions<SimulatorOptions> options,
        TimeProvider time,
        IServer server,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration,
        ILogger<TelemetrySimulator> logger)
    {
        _registry = registry;
        _control = control;
        _world = world;
        _options = options.Value;
        _time = time;
        _server = server;
        _lifetime = lifetime;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Devices can only call the API once Kestrel is listening.
            await WaitForStartAsync(stoppingToken);
            _http.BaseAddress = ResolveBaseAddress();
            _logger.LogInformation("Device simulator posting telemetry over HTTP to {BaseAddress}", _http.BaseAddress);

            _nextAutoEvent = _time.GetUtcNow().AddSeconds(Math.Max(10, _options.AutoEventMinSeconds / 2));
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Simulator tick failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    private async Task WaitForStartAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = _lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
    }

    private Uri ResolveBaseAddress()
    {
        string? configured = _configuration["Simulator:ApiBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new Uri(configured.EndsWith('/') ? configured : configured + "/");
        }

        ICollection<string>? addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        string address = addresses?.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                         ?? addresses?.FirstOrDefault()
                         ?? "http://localhost:5100";

        // Wildcard bindings ("http://[::]:8080", "http://+:8080", "http://0.0.0.0:8080") are reached via localhost.
        address = address
            .Replace("://[::]", "://localhost", StringComparison.Ordinal)
            .Replace("://+", "://localhost", StringComparison.Ordinal)
            .Replace("://*", "://localhost", StringComparison.Ordinal)
            .Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal);

        return new Uri(address.TrimEnd('/') + "/");
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        if (!_control.Enabled)
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();

        if (now - _lastShadowSync >= TimeSpan.FromSeconds(1))
        {
            await SyncShadowsAsync(cancellationToken);
            _lastShadowSync = now;
        }

        _world.Advance(now, _deviceStates);

        IReadOnlyList<SensorDevice> devices = _registry.All();
        var sends = new List<Task>();

        foreach (SensorDevice device in devices)
        {
            if (!device.IsSimulated)
            {
                continue;
            }

            bool due = !_nextDue.TryGetValue(device.MacAddress, out DateTimeOffset next) || now >= next;

            // An actuator reports immediately after its state changes, so commands are confirmed fast.
            bool stateChanged = device.IsActuator &&
                                _deviceStates.TryGetValue(device.MacAddress, out bool state) &&
                                (!_lastReported.TryGetValue(device.MacAddress, out bool reported) || reported != state);

            if (!due && !stateChanged)
            {
                continue;
            }

            double jitter = 0.85 + (Random.Shared.NextDouble() * 0.3);
            _nextDue[device.MacAddress] = now.AddSeconds(device.ExpectedIntervalSeconds * jitter);

            if (_control.IsSilenced(device, now))
            {
                continue; // simulated outage / disconnect: the device stops publishing
            }

            sends.Add(PublishAsync(device, now, cancellationToken));
            _control.CountGenerated();
        }

        await Task.WhenAll(sends);

        if (_options.AutoEvents && now >= _nextAutoEvent)
        {
            TriggerAutoEvent(devices, now);
            int min = Math.Max(10, _options.AutoEventMinSeconds);
            int max = Math.Max(min + 1, _options.AutoEventMaxSeconds);
            _nextAutoEvent = now.AddSeconds(Random.Shared.Next(min, max));
        }
    }

    private async Task SyncShadowsAsync(CancellationToken cancellationToken)
    {
        try
        {
            ActuatorShadowDto[]? shadows = await _http.GetFromJsonAsync<ActuatorShadowDto[]>("api/commands/shadows", cancellationToken);
            foreach (ActuatorShadowDto shadow in shadows ?? Array.Empty<ActuatorShadowDto>())
            {
                if (!_control.IsSilencedMac(shadow.Mac, shadow.SubZoneId, _time.GetUtcNow()))
                {
                    _deviceStates[shadow.Mac] = shadow.Desired; // the device adopts the desired state
                }
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Shadow sync failed; devices keep their last state.");
        }
    }

    private async Task PublishAsync(SensorDevice device, DateTimeOffset now, CancellationToken cancellationToken)
    {
        bool spike = _control.TryConsumeSpike(device.MacAddress);
        object payload;

        switch (device.ValueKind)
        {
            case TelemetryValueKind.Float:
            {
                double value = device.Metric == "soil_moisture" && _world.HasSoilModel(device.Location.SubZoneId)
                    ? _world.SoilMoisture(device, spike)
                    : SignalModels.Sample(SignalModels.For(device.Metric), device.MacAddress, now, spike);
                payload = new { deviceMac = device.MacAddress, payloadType = "float", value = (float)Clamp(device, value), timestamp = now };
                break;
            }

            case TelemetryValueKind.Int:
            {
                double value = _world.PumpLoad(device, spike)
                               ?? SignalModels.Sample(SignalModels.For(device.Metric), device.MacAddress, now, spike);
                payload = new { deviceMac = device.MacAddress, payloadType = "int", value = (int)Math.Round(Clamp(device, value)), timestamp = now };
                break;
            }

            default:
            {
                bool state = _deviceStates.TryGetValue(device.MacAddress, out bool held) && held;

                // A queued "spike" on an actuator makes it chatter: report the opposite state for one reading.
                bool reported = spike ? !state : state;
                _lastReported[device.MacAddress] = state;
                payload = new { deviceMac = device.MacAddress, payloadType = "bool", value = reported, timestamp = now };
                break;
            }
        }

        try
        {
            using HttpResponseMessage response = await _http.PostAsJsonAsync("api/telemetry", payload, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _consecutiveFailures = 0;
            }
            else if ((int)response.StatusCode >= 500)
            {
                _logger.LogDebug("Gateway answered {Status} for {Device}", (int)response.StatusCode, device.DisplayName);
            }
        }
        catch (HttpRequestException ex)
        {
            if (++_consecutiveFailures % 50 == 1)
            {
                _logger.LogWarning(ex, "Simulated device could not reach the API at {BaseAddress}", _http.BaseAddress);
            }
        }
    }

    private static double Clamp(SensorDevice device, double value) =>
        Math.Clamp(value, device.MinValue ?? double.MinValue, device.MaxValue ?? double.MaxValue);

    private void TriggerAutoEvent(IReadOnlyList<SensorDevice> devices, DateTimeOffset now)
    {
        List<SensorDevice> simulated = devices.Where(d => d.IsSimulated).ToList();
        if (simulated.Count == 0)
        {
            return;
        }

        double roll = Random.Shared.NextDouble();

        if (roll < 0.5)
        {
            List<SensorDevice> numeric = simulated.Where(d => !d.IsActuator).ToList();
            if (numeric.Count > 0)
            {
                SensorDevice target = numeric[Random.Shared.Next(numeric.Count)];
                _control.QueueSpike(target.MacAddress, 4);
                _logger.LogInformation("Auto event: spike queued for {Device}", target.DisplayName);
            }
        }
        else if (roll < 0.8)
        {
            SensorDevice target = simulated[Random.Shared.Next(simulated.Count)];
            _control.Disconnect(target.MacAddress, TimeSpan.FromSeconds(25), now);
            _logger.LogInformation("Auto event: {Device} disconnected for 25s", target.DisplayName);
        }
        else
        {
            string subZone = simulated[Random.Shared.Next(simulated.Count)].Location.SubZoneId;
            _control.StartOutage(subZone, TimeSpan.FromSeconds(30), now);
            _logger.LogInformation("Auto event: gateway outage in {SubZone} for 30s", subZone);
        }
    }
}
