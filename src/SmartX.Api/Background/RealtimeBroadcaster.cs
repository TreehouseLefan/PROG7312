using Microsoft.AspNetCore.SignalR;
using SmartX.Api.Hubs;
using SmartX.Api.Services;
using SmartX.Core.Contracts;
using SmartX.Core.Pulse;

namespace SmartX.Api.Background;

/// <summary>
/// Pushes every live view to the dashboards over SignalR.
/// <list type="bullet">
/// <item>Every 500 ms: newly processed packets and queue statistics.</item>
/// <item>Every second: Pulse Grid changes, offline re-reports into the alert HashSets, and an engine refresh.</item>
/// <item>Only when their version number changed: the alert board, command state, engine output and history.</item>
/// <item>Immediately: each new alert, via the <see cref="AlertCenter.AlertRaised"/> event.</item>
/// </list>
/// </summary>
public sealed class RealtimeBroadcaster : BackgroundService
{
    private readonly PulseMonitor _pulse;
    private readonly IncidentBook _incidents;
    private readonly TelemetryIngestionService _ingestion;
    private readonly IngestPipeline _pipeline;
    private readonly AlertCenter _alerts;
    private readonly CommandService _commands;
    private readonly ActionEngine _engine;
    private readonly TimelineService _timeline;
    private readonly DeviceRegistry _registry;
    private readonly IHubContext<PulseHub> _hub;
    private readonly TimeProvider _time;
    private readonly ILogger<RealtimeBroadcaster> _logger;

    private long _alertsVersion = -1;
    private long _commandsVersion = -1;
    private long _engineVersion = -1;
    private long _historyVersion = -1;

    public RealtimeBroadcaster(
        PulseMonitor pulse,
        IncidentBook incidents,
        TelemetryIngestionService ingestion,
        IngestPipeline pipeline,
        AlertCenter alerts,
        CommandService commands,
        ActionEngine engine,
        TimelineService timeline,
        DeviceRegistry registry,
        IHubContext<PulseHub> hub,
        TimeProvider time,
        ILogger<RealtimeBroadcaster> logger)
    {
        _pulse = pulse;
        _incidents = incidents;
        _ingestion = ingestion;
        _pipeline = pipeline;
        _alerts = alerts;
        _commands = commands;
        _engine = engine;
        _timeline = timeline;
        _registry = registry;
        _hub = hub;
        _time = time;
        _logger = logger;

        _alerts.AlertRaised += PushAlertNow;
    }

    public override void Dispose()
    {
        _alerts.AlertRaised -= PushAlertNow;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        long tick = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                tick++;
                try
                {
                    await BroadcastStreamAsync(stoppingToken);

                    if (tick % 2 == 0)
                    {
                        await BroadcastPulseAsync(stoppingToken);
                        _engine.Refresh();
                    }

                    await BroadcastChangesAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Real-time broadcast failed; retrying on the next tick.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private Task BroadcastStreamAsync(CancellationToken cancellationToken)
    {
        var batch = new StreamBatchDto(_time.GetUtcNow(), _pipeline.DrainPendingPush(120), _pipeline.Stats());
        return _hub.Clients.All.SendAsync(PulseHub.StreamMethod, batch, cancellationToken);
    }

    private async Task BroadcastPulseAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        (PulseTileDto[] changes, PulseTileDto[] all) = _pulse.Collect();

        // Level-triggered: every offline device is re-reported each second; the HashSet absorbs duplicates.
        var offline = new List<SensorDevice>();
        foreach (PulseTileDto tile in all)
        {
            if (tile.State == PulseState.Offline && _registry.TryGet(tile.Mac, out SensorDevice? device))
            {
                offline.Add(device);
            }
        }

        _alerts.SyncOffline(offline, now);
        _alerts.Sweep(now, key => _registry.TryGet(key, out SensorDevice? d) ? d : null);

        var batch = new PulseBatchDto(
            now,
            changes,
            _pulse.GetInsights(all),
            _ingestion.GetStats(),
            _incidents.OpenCount,
            _incidents.MedianSecondsToAcknowledge());

        await _hub.Clients.All.SendAsync(PulseHub.PulseMethod, batch, cancellationToken);
    }

    private async Task BroadcastChangesAsync(CancellationToken cancellationToken)
    {
        long alerts = _alerts.Version;
        if (alerts != _alertsVersion)
        {
            _alertsVersion = alerts;
            await _hub.Clients.All.SendAsync(PulseHub.AlertsMethod, _alerts.Board(), cancellationToken);
        }

        long commands = _commands.Version;
        if (commands != _commandsVersion)
        {
            _commandsVersion = commands;
            await _hub.Clients.All.SendAsync(PulseHub.CommandsMethod, new CommandStateDto(_commands.History(), _commands.Shadows()), cancellationToken);
        }

        long engine = _engine.Version;
        if (engine != _engineVersion)
        {
            _engineVersion = engine;
            await _hub.Clients.All.SendAsync(PulseHub.EngineMethod, _engine.Snapshot(), cancellationToken);
        }

        long history = _timeline.HistoryVersion;
        if (history != _historyVersion)
        {
            _historyVersion = history;
            DateTimeOffset now = _time.GetUtcNow();
            await _hub.Clients.All.SendAsync(PulseHub.HistoryMethod, _timeline.History(now.AddHours(-24), now.AddMinutes(1), null, 60), cancellationToken);
        }
    }

    private void PushAlertNow(AlertDto alert)
    {
        // Fire-and-forget so the packet that raised the alert is not slowed down by the network.
        _ = Task.Run(async () =>
        {
            try
            {
                await _hub.Clients.All.SendAsync(PulseHub.AlertMethod, alert);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Immediate alert push failed.");
            }
        });
    }
}
