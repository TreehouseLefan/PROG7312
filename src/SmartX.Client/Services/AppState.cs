using Microsoft.AspNetCore.SignalR.Client;
using SmartX.Core.Contracts;
using SmartX.Core.Pulse;
using SmartX.Core.Streaming;
using SmartX.Core.Telemetry;

namespace SmartX.Client.Services;

/// <summary>Filters and selections on the Command Stream page, kept while the user visits other modules.</summary>
public sealed class CommandStreamUiState
{
    public string Query { get; set; } = string.Empty;
    public SensorCategory? Category { get; set; }
    public string State { get; set; } = "Any";
    public bool AlertsOnly { get; set; }
    public string? SelectedMac { get; set; }
    public SensorDeviceDto? SelectedDevice { get; set; }
    public int TimelineMinutes { get; set; } = 15;
    public bool PriorityOnly { get; set; }
    public bool FeedFrozen { get; set; }
    public HistoryKind? HistoryFilter { get; set; }
    public string BulkScope { get; set; } = "sz-c";
    public DeviceSearchResponseDto? LastResults { get; set; }
    public BurstReportDto? LastBurst { get; set; }
}

/// <summary>
/// Application-wide state store (Blazor WebAssembly runs in one browser thread, so a singleton is safe).
/// <para>It owns the one SignalR connection and keeps every module's live data up to date in the
/// background, whichever page is showing. Pages render straight from this cache, so switching between
/// the Pulse Grid, the Command Stream and the other views is instant and nothing is lost: filters,
/// the selected device, the stream feed and alert toasts all survive navigation.</para>
/// </summary>
public sealed class AppState : IDisposable
{
    private const int FeedCapacity = 80;
    private const int ToastCapacity = 4;

    private readonly SmartXApiClient _api;
    private readonly PulseHubClient _hub;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private bool _started;

    public AppState(SmartXApiClient api, PulseHubClient hub)
    {
        _api = api;
        _hub = hub;
    }

    /// <summary>Raised whenever any cached data changes. Components re-render on it.</summary>
    public event Action? Changed;

    // ---------- Connection ----------
    public HubConnectionState Connection { get; private set; } = HubConnectionState.Disconnected;
    public DateTime? LastPulseUtc { get; private set; }
    public DateTime? LastStreamUtc { get; private set; }
    public string? StartupError { get; private set; }
    public bool Loaded { get; private set; }

    // ---------- Part 1: Pulse Grid ----------
    public Dictionary<string, PulseTileDto> Tiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public GridGroupDto[] Groups { get; private set; } = Array.Empty<GridGroupDto>();
    public Dictionary<string, ZoneInsightDto> ZoneInsights { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> PulseUntil { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IngestStatsDto? IngestStats { get; private set; }
    public int OpenIncidents { get; private set; }
    public double? MedianSecondsToAcknowledge { get; private set; }
    public long IncidentVersion { get; private set; }
    public string OperatorName { get; set; } = "ops-engineer";

    // ---------- Part 2: Command Stream ----------
    public CommandStreamUiState CommandUi { get; } = new();
    public LinkedList<StreamPacketDto> Feed { get; } = new();
    public QueueStatsDto? Queue { get; private set; }
    public AlertBoardDto? Alerts { get; private set; }
    public CommandHistoryDto? Commands { get; private set; }
    public Dictionary<string, ActuatorShadowDto> Shadows { get; } = new(StringComparer.OrdinalIgnoreCase);
    public EngineSnapshotDto? Engine { get; private set; }
    public HistoryEventDto[] History { get; private set; } = Array.Empty<HistoryEventDto>();
    public List<AlertDto> Toasts { get; } = new();

    public int CriticalAlertCount => Alerts?.Active.Count(a => a.Severity == AlertSeverity.Critical) ?? 0;

    public int ActiveAlertCount => Alerts?.Active.Length ?? 0;

    /// <summary>Loads every module once, then keeps it live through SignalR. Safe to call from every page.</summary>
    public async Task EnsureStartedAsync()
    {
        if (_started)
        {
            return;
        }

        await _startLock.WaitAsync();
        try
        {
            if (_started)
            {
                return;
            }

            _hub.PulseReceived += OnPulse;
            _hub.IncidentReceived += OnIncident;
            _hub.StreamReceived += OnStream;
            _hub.AlertReceived += OnAlert;
            _hub.AlertsReceived += OnAlerts;
            _hub.CommandsReceived += OnCommands;
            _hub.EngineReceived += OnEngine;
            _hub.HistoryReceived += OnHistory;
            _hub.ConnectionChanged += OnConnectionChanged;
            _started = true;

            await LoadSnapshotsAsync();

            try
            {
                await _hub.EnsureStartedAsync();
                Connection = _hub.State;
            }
            catch (Exception ex)
            {
                StartupError = $"Live updates are unavailable ({ex.Message}). Is SmartX.Api running at {_api.BaseUrl}?";
            }

            NotifyChanged();
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Reloads everything from the REST API (used after a reconnect or on demand).</summary>
    public async Task LoadSnapshotsAsync()
    {
        try
        {
            Task<PulseSnapshotDto?> pulse = _api.GetPulseSnapshotAsync();
            Task<AlertBoardDto?> alerts = _api.GetAlertBoardAsync();
            Task<CommandHistoryDto?> commands = _api.GetCommandHistoryAsync();
            Task<ActuatorShadowDto[]?> shadows = _api.GetShadowsAsync();
            Task<EngineSnapshotDto?> engine = _api.GetEngineAsync();
            Task<HistoryEventDto[]?> history = _api.GetHistoryAsync(24 * 60, null, 60);
            Task<QueueStatsDto?> queue = _api.GetQueueStatsAsync();
            Task<StreamPacketDto[]?> recent = _api.GetRecentPacketsAsync(FeedCapacity);

            await Task.WhenAll(pulse, alerts, commands, shadows, engine, history, queue, recent);

            if (pulse.Result is PulseSnapshotDto snapshot)
            {
                Groups = snapshot.Groups;
                Tiles.Clear();
                foreach (PulseTileDto tile in snapshot.Tiles)
                {
                    Tiles[tile.Mac] = tile;
                }

                ApplyZoneInsights(snapshot.Insights);
                IngestStats = snapshot.Stats;
                OpenIncidents = snapshot.OpenIncidents;
                MedianSecondsToAcknowledge = snapshot.MedianSecondsToAcknowledge;
            }

            Alerts = alerts.Result ?? Alerts;
            Commands = commands.Result ?? Commands;
            ApplyShadows(shadows.Result);
            Engine = engine.Result ?? Engine;
            History = history.Result ?? History;
            Queue = queue.Result ?? Queue;

            if (recent.Result is StreamPacketDto[] packets && Feed.Count == 0)
            {
                // The API returns newest first; the feed keeps newest first too.
                foreach (StreamPacketDto packet in packets)
                {
                    Feed.AddLast(packet);
                }
            }

            StartupError = null;
            Loaded = true;
        }
        catch (Exception ex)
        {
            StartupError = $"The API could not be reached at {_api.BaseUrl} ({ex.Message}). Start SmartX.Api first.";
        }

        NotifyChanged();
    }

    public void DismissToast(AlertDto alert)
    {
        Toasts.Remove(alert);
        NotifyChanged();
    }

    public void ApplyCommandResult(CommandResultDto result)
    {
        Commands = result.History;
        NotifyChanged();
    }

    public void NotifyChanged() => Changed?.Invoke();

    // ---------- SignalR handlers ----------

    private void OnPulse(PulseBatchDto batch)
    {
        DateTime nowUtc = DateTime.UtcNow;
        foreach (PulseTileDto change in batch.Changes)
        {
            if (Tiles.TryGetValue(change.Mac, out PulseTileDto? previous) &&
                previous.State != change.State &&
                change.State is PulseState.Spike or PulseState.Offline)
            {
                PulseUntil[change.Mac] = nowUtc.AddSeconds(3);
            }

            Tiles[change.Mac] = change;
        }

        ApplyZoneInsights(batch.Insights);
        IngestStats = batch.Stats;
        if (OpenIncidents != batch.OpenIncidents)
        {
            IncidentVersion++;
        }

        OpenIncidents = batch.OpenIncidents;
        MedianSecondsToAcknowledge = batch.MedianSecondsToAcknowledge;
        LastPulseUtc = nowUtc;
        NotifyChanged();
    }

    private void OnIncident(IncidentDto incident)
    {
        IncidentVersion++;
        NotifyChanged();
    }

    private void OnStream(StreamBatchDto batch)
    {
        Queue = batch.Queue;
        LastStreamUtc = DateTime.UtcNow;

        if (!CommandUi.FeedFrozen)
        {
            foreach (StreamPacketDto packet in batch.Packets)
            {
                Feed.AddFirst(packet);
            }

            while (Feed.Count > FeedCapacity)
            {
                Feed.RemoveLast();
            }
        }

        NotifyChanged();
    }

    private void OnAlert(AlertDto alert)
    {
        if (alert.Severity is AlertSeverity.Critical or AlertSeverity.High)
        {
            Toasts.Insert(0, alert);
            while (Toasts.Count > ToastCapacity)
            {
                Toasts.RemoveAt(Toasts.Count - 1);
            }

            _ = ExpireToastAsync(alert);
        }

        NotifyChanged();
    }

    private async Task ExpireToastAsync(AlertDto alert)
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (Toasts.Remove(alert))
        {
            NotifyChanged();
        }
    }

    private void OnAlerts(AlertBoardDto board)
    {
        Alerts = board;
        NotifyChanged();
    }

    private void OnCommands(CommandStateDto state)
    {
        Commands = state.History;
        ApplyShadows(state.Shadows);
        NotifyChanged();
    }

    private void OnEngine(EngineSnapshotDto snapshot)
    {
        Engine = snapshot;
        NotifyChanged();
    }

    private void OnHistory(HistoryEventDto[] events)
    {
        History = events;
        NotifyChanged();
    }

    private void OnConnectionChanged(HubConnectionState state)
    {
        bool reconnected = Connection == HubConnectionState.Reconnecting && state == HubConnectionState.Connected;
        Connection = state;
        NotifyChanged();

        if (reconnected)
        {
            // Anything missed while offline is fetched again so the views stay consistent.
            _ = LoadSnapshotsAsync();
        }
    }

    private void ApplyZoneInsights(IEnumerable<ZoneInsightDto> insights)
    {
        ZoneInsights.Clear();
        foreach (ZoneInsightDto insight in insights)
        {
            ZoneInsights[insight.SubZoneId] = insight;
        }
    }

    private void ApplyShadows(ActuatorShadowDto[]? shadows)
    {
        if (shadows is null)
        {
            return;
        }

        Shadows.Clear();
        foreach (ActuatorShadowDto shadow in shadows)
        {
            Shadows[shadow.Mac] = shadow;
        }
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _hub.PulseReceived -= OnPulse;
        _hub.IncidentReceived -= OnIncident;
        _hub.StreamReceived -= OnStream;
        _hub.AlertReceived -= OnAlert;
        _hub.AlertsReceived -= OnAlerts;
        _hub.CommandsReceived -= OnCommands;
        _hub.EngineReceived -= OnEngine;
        _hub.HistoryReceived -= OnHistory;
        _hub.ConnectionChanged -= OnConnectionChanged;
    }
}
