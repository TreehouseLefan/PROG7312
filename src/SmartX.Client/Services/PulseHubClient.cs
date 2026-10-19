using Microsoft.AspNetCore.SignalR.Client;
using SmartX.Core.Contracts;

namespace SmartX.Client.Services;

/// <summary>
/// The single SignalR connection shared by every module of the dashboard. It is opened once by
/// <see cref="AppState"/> and stays open while the user moves between pages, so no view ever has to
/// reconnect or reload when it is shown again.
/// </summary>
public sealed class PulseHubClient : IAsyncDisposable
{
    private readonly ApiSettings _settings;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private HubConnection? _connection;

    public PulseHubClient(ApiSettings settings)
    {
        _settings = settings;
    }

    public event Action<PulseBatchDto>? PulseReceived;
    public event Action<IncidentDto>? IncidentReceived;
    public event Action<StreamBatchDto>? StreamReceived;
    public event Action<AlertDto>? AlertReceived;
    public event Action<AlertBoardDto>? AlertsReceived;
    public event Action<CommandStateDto>? CommandsReceived;
    public event Action<EngineSnapshotDto>? EngineReceived;
    public event Action<HistoryEventDto[]>? HistoryReceived;
    public event Action<HubConnectionState>? ConnectionChanged;

    public HubConnectionState State => _connection?.State ?? HubConnectionState.Disconnected;

    public async Task EnsureStartedAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            if (_connection is null)
            {
                _connection = new HubConnectionBuilder()
                    .WithUrl(_settings.BaseUrl + "hubs/pulse")
                    .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15) })
                    .Build();

                _connection.On<PulseBatchDto>("pulse", batch => PulseReceived?.Invoke(batch));
                _connection.On<IncidentDto>("incident", incident => IncidentReceived?.Invoke(incident));
                _connection.On<StreamBatchDto>("stream", batch => StreamReceived?.Invoke(batch));
                _connection.On<AlertDto>("alert", alert => AlertReceived?.Invoke(alert));
                _connection.On<AlertBoardDto>("alerts", board => AlertsReceived?.Invoke(board));
                _connection.On<CommandStateDto>("commands", state => CommandsReceived?.Invoke(state));
                _connection.On<EngineSnapshotDto>("engine", snapshot => EngineReceived?.Invoke(snapshot));
                _connection.On<HistoryEventDto[]>("history", events => HistoryReceived?.Invoke(events));

                _connection.Reconnecting += _ =>
                {
                    ConnectionChanged?.Invoke(HubConnectionState.Reconnecting);
                    return Task.CompletedTask;
                };

                _connection.Reconnected += _ =>
                {
                    ConnectionChanged?.Invoke(HubConnectionState.Connected);
                    return Task.CompletedTask;
                };

                _connection.Closed += _ =>
                {
                    ConnectionChanged?.Invoke(HubConnectionState.Disconnected);
                    return Task.CompletedTask;
                };
            }

            if (_connection.State == HubConnectionState.Disconnected)
            {
                ConnectionChanged?.Invoke(HubConnectionState.Connecting);
                await _connection.StartAsync();
                ConnectionChanged?.Invoke(_connection.State);
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _startLock.Dispose();
    }
}
