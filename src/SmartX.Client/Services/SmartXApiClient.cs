using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Forms;
using SmartX.Core.Contracts;
using SmartX.Core.Telemetry;
using SmartX.Core.Topology;

namespace SmartX.Client.Services;

/// <summary>Typed wrapper around every Smart-X API endpoint the dashboard uses.</summary>
public sealed class SmartXApiClient
{
    public const long MaxUploadBytes = 10 * 1024 * 1024;

    private readonly HttpClient _http;

    public SmartXApiClient(HttpClient http)
    {
        _http = http;
    }

    public string BaseUrl => _http.BaseAddress?.ToString() ?? string.Empty;

    public string AttachmentUrl(Guid id) => $"{BaseUrl}api/attachments/{id}/content";

    private static string Mac(string mac) => Uri.EscapeDataString(mac);

    // ---------- System ----------
    public Task<HealthDto?> GetHealthAsync() => _http.GetFromJsonAsync<HealthDto>("api/health");

    public Task<PillarDto[]?> GetPillarsAsync() => _http.GetFromJsonAsync<PillarDto[]>("api/pillars");

    // ---------- Devices ----------
    public Task<SensorDeviceDto[]?> GetDevicesAsync() => _http.GetFromJsonAsync<SensorDeviceDto[]>("api/devices");

    public async Task<SensorDeviceDto?> GetDeviceAsync(string mac)
    {
        using HttpResponseMessage response = await _http.GetAsync($"api/devices/{Mac(mac)}");
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<SensorDeviceDto>() : null;
    }

    public async Task<ApiResult<SensorDeviceDto>> RegisterDeviceAsync(RegisterDeviceRequest request)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync("api/devices", request);
        return await ApiResult<SensorDeviceDto>.FromResponseAsync(response);
    }

    public Task<DeviceTelemetryDto?> GetDeviceTelemetryAsync(string mac, int max = 120) =>
        _http.GetFromJsonAsync<DeviceTelemetryDto>($"api/devices/{Mac(mac)}/telemetry?max={max}");

    public Task<PlacementResultDto?> GetDevicePlacementAsync(string mac) =>
        _http.GetFromJsonAsync<PlacementResultDto>($"api/devices/{Mac(mac)}/placement");

    public Task<AttachmentDto[]?> GetAttachmentsAsync(string mac) =>
        _http.GetFromJsonAsync<AttachmentDto[]>($"api/devices/{Mac(mac)}/attachments");

    public async Task<ApiResult<AttachmentDto>> UploadAttachmentAsync(string mac, IBrowserFile file, string kind)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(file.OpenReadStream(MaxUploadBytes));

        string contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        try
        {
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
        catch (FormatException)
        {
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        content.Add(fileContent, "file", file.Name);
        content.Add(new StringContent(kind), "kind");

        using HttpResponseMessage response = await _http.PostAsync($"api/devices/{Mac(mac)}/attachments", content);
        return await ApiResult<AttachmentDto>.FromResponseAsync(response);
    }

    // ---------- Telemetry ----------
    public async Task<ApiResult<TelemetryIngestResult>> SendReadingAsync(TelemetryIngestRequest request)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync("api/telemetry", request);
        return await ApiResult<TelemetryIngestResult>.FromResponseAsync(response);
    }

    public async Task<ApiResult<TelemetryBatchResult>> SendBatchAsync(TelemetryBatchRequest request)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync("api/telemetry/batch", request);
        return await ApiResult<TelemetryBatchResult>.FromResponseAsync(response);
    }

    // ---------- Pulse grid & incidents ----------
    public Task<PulseSnapshotDto?> GetPulseSnapshotAsync() => _http.GetFromJsonAsync<PulseSnapshotDto>("api/pulse/snapshot");

    public Task<IncidentDto[]?> GetIncidentsAsync(bool openOnly, string? mac = null)
    {
        string url = $"api/incidents?openOnly={(openOnly ? "true" : "false")}";
        if (!string.IsNullOrWhiteSpace(mac))
        {
            url += $"&mac={Mac(mac)}";
        }

        return _http.GetFromJsonAsync<IncidentDto[]>(url);
    }

    public async Task<ApiResult<IncidentDto>> AcknowledgeIncidentAsync(Guid id, string operatorName)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            $"api/incidents/{id}/acknowledge",
            new AcknowledgeRequest(operatorName));
        return await ApiResult<IncidentDto>.FromResponseAsync(response);
    }

    // ---------- Topology ----------
    public Task<List<DeploymentNode>?> GetTopologyAsync() => _http.GetFromJsonAsync<List<DeploymentNode>>("api/topology");

    public Task<LocationOptionDto[]?> GetLocationsAsync() => _http.GetFromJsonAsync<LocationOptionDto[]>("api/topology/locations");

    public Task<TopologyValidationResultDto?> ValidateLiveTopologyAsync() =>
        _http.GetFromJsonAsync<TopologyValidationResultDto>("api/topology/validate");

    public async Task<ApiResult<TopologyValidationResultDto>> ValidateTreeAsync(List<DeploymentNode> roots)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync("api/topology/validate", roots);
        return await ApiResult<TopologyValidationResultDto>.FromResponseAsync(response);
    }

    public Task<List<DeploymentNode>?> GetBrokenSampleAsync() =>
        _http.GetFromJsonAsync<List<DeploymentNode>>("api/topology/samples/invalid");

    public Task<PlacementResultDto?> GetNodePlacementAsync(string nodeId) =>
        _http.GetFromJsonAsync<PlacementResultDto>($"api/topology/nodes/{Uri.EscapeDataString(nodeId)}/placement");

    // ---------- Analytics & diagnostics ----------
    public Task<PowerMeterDto[]?> GetMetersAsync() => _http.GetFromJsonAsync<PowerMeterDto[]>("api/meters");

    public async Task<ApiResult<MeterAggregationDto>> AggregateMetersAsync(IEnumerable<string> ids, double tolerance)
    {
        string query = string.Join("&", ids.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => $"ids={Mac(i)}"));
        string url = $"api/meters/aggregate?tolerance={tolerance.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        if (query.Length > 0)
        {
            url += "&" + query;
        }

        using HttpResponseMessage response = await _http.GetAsync(url);
        return await ApiResult<MeterAggregationDto>.FromResponseAsync(response);
    }

    public async Task<ApiResult<HistoryMatrixDto>> GetHistoryMatrixAsync(string subZoneId, int slots)
    {
        using HttpResponseMessage response = await _http.GetAsync($"api/zones/{Uri.EscapeDataString(subZoneId)}/history-matrix?slots={slots}");
        return await ApiResult<HistoryMatrixDto>.FromResponseAsync(response);
    }

    public Task<BoxingReportDto?> RunBoxingComparisonAsync(int count) =>
        _http.GetFromJsonAsync<BoxingReportDto>($"api/diagnostics/boxing?count={count}");

    public async Task<ApiResult<BatchDemoDto>> RunBatchDemoAsync(int devices, int batches, int readings)
    {
        using HttpResponseMessage response = await _http.PostAsync(
            $"api/diagnostics/batch-demo?devices={devices}&batches={batches}&readings={readings}",
            content: null);
        return await ApiResult<BatchDemoDto>.FromResponseAsync(response);
    }

    // ---------- Simulator ----------
    public Task<SimulatorStatusDto?> GetSimulatorStatusAsync() => _http.GetFromJsonAsync<SimulatorStatusDto>("api/simulator/status");

    public async Task<ApiResult<SimulatorStatusDto>> SetSimulatorAsync(bool enabled)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync("api/simulator/toggle", new SimulatorToggleRequest(enabled));
        return await ApiResult<SimulatorStatusDto>.FromResponseAsync(response);
    }

    public Task<ApiResult<SimulatorStatusDto>> TriggerOutageAsync(string subZoneId, int seconds = 30) =>
        PostSimulatorAsync($"api/simulator/outage/{Uri.EscapeDataString(subZoneId)}?seconds={seconds}");

    public Task<ApiResult<SimulatorStatusDto>> TriggerSpikeAsync(string mac, int readings = 4) =>
        PostSimulatorAsync($"api/simulator/spike/{Mac(mac)}?readings={readings}");

    public Task<ApiResult<SimulatorStatusDto>> TriggerDisconnectAsync(string mac, int seconds = 25) =>
        PostSimulatorAsync($"api/simulator/disconnect/{Mac(mac)}?seconds={seconds}");

    public Task<ApiResult<SimulatorStatusDto>> TriggerDryOutAsync(string subZoneId) =>
        PostSimulatorAsync($"api/simulator/dryout/{Uri.EscapeDataString(subZoneId)}");

    // ---------- Part 2: command stream ----------
    public async Task<ApiResult<DeviceSearchResponseDto>> SearchDevicesAsync(
        string? query,
        SensorCategory? category,
        string? state,
        bool alertsOnly,
        bool record)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(query))
        {
            parts.Add("query=" + Uri.EscapeDataString(query.Trim()));
        }

        if (category is SensorCategory c)
        {
            parts.Add("category=" + c);
        }

        if (!string.IsNullOrWhiteSpace(state) && state != "Any")
        {
            parts.Add("state=" + Uri.EscapeDataString(state));
        }

        if (alertsOnly)
        {
            parts.Add("alertsOnly=true");
        }

        if (record)
        {
            parts.Add("record=true");
        }

        string url = "api/stream/devices" + (parts.Count > 0 ? "?" + string.Join("&", parts) : string.Empty);
        using HttpResponseMessage response = await _http.GetAsync(url);
        return await ApiResult<DeviceSearchResponseDto>.FromResponseAsync(response);
    }

    public async Task InspectDeviceAsync(string mac)
    {
        using HttpResponseMessage response = await _http.PostAsync($"api/stream/devices/{Mac(mac)}/inspect", content: null);
    }

    public Task<TimelineDto?> GetTimelineAsync(string mac, int minutes, int maxPoints) =>
        _http.GetFromJsonAsync<TimelineDto>($"api/devices/{Mac(mac)}/timeline?minutes={minutes}&maxPoints={maxPoints}");

    public Task<QueueStatsDto?> GetQueueStatsAsync() => _http.GetFromJsonAsync<QueueStatsDto>("api/stream/queue");

    public Task<StreamPacketDto[]?> GetRecentPacketsAsync(int max) => _http.GetFromJsonAsync<StreamPacketDto[]>($"api/stream/recent?max={max}");

    public async Task<ApiResult<QueueStatsDto>> SetQueuePausedAsync(bool paused)
    {
        using HttpResponseMessage response = await _http.PostAsync(paused ? "api/stream/queue/pause" : "api/stream/queue/resume", content: null);
        return await ApiResult<QueueStatsDto>.FromResponseAsync(response);
    }

    public async Task<ApiResult<BurstReportDto>> RunBurstAsync(int count)
    {
        using HttpResponseMessage response = await _http.PostAsync($"api/stream/burst?count={count}", content: null);
        return await ApiResult<BurstReportDto>.FromResponseAsync(response);
    }

    public Task<HistoryEventDto[]?> GetHistoryAsync(int minutes, HistoryKind? kind, int max)
    {
        string url = $"api/history?minutes={minutes}&max={max}";
        if (kind is HistoryKind k)
        {
            url += "&kind=" + k;
        }

        return _http.GetFromJsonAsync<HistoryEventDto[]>(url);
    }

    // ---------- Part 2: commands ----------
    public Task<CommandHistoryDto?> GetCommandHistoryAsync() => _http.GetFromJsonAsync<CommandHistoryDto>("api/commands/history");

    public Task<ActuatorShadowDto[]?> GetShadowsAsync() => _http.GetFromJsonAsync<ActuatorShadowDto[]>("api/commands/shadows");

    public async Task<ApiResult<CommandResultDto>> IssueCommandAsync(IssueCommandRequest request)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync("api/commands", request);
        return await ReadCommandResultAsync(response);
    }

    public async Task<ApiResult<CommandResultDto>> UndoAsync(string issuedBy)
    {
        using HttpResponseMessage response = await _http.PostAsync($"api/commands/undo?issuedBy={Uri.EscapeDataString(issuedBy)}", content: null);
        return await ReadCommandResultAsync(response);
    }

    public async Task<ApiResult<CommandResultDto>> RedoAsync(string issuedBy)
    {
        using HttpResponseMessage response = await _http.PostAsync($"api/commands/redo?issuedBy={Uri.EscapeDataString(issuedBy)}", content: null);
        return await ReadCommandResultAsync(response);
    }

    /// <summary>Command failures still return a CommandResultDto body (422 / 409) with the reason.</summary>
    private static async Task<ApiResult<CommandResultDto>> ReadCommandResultAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode && (int)response.StatusCode is 409 or 422)
        {
            try
            {
                CommandResultDto? body = await response.Content.ReadFromJsonAsync<CommandResultDto>();
                if (body is not null)
                {
                    return ApiResult<CommandResultDto>.Failure(body.Message);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Fall through to the generic reader.
            }
        }

        return await ApiResult<CommandResultDto>.FromResponseAsync(response);
    }

    // ---------- Part 2: alerts and engine ----------
    public Task<AlertBoardDto?> GetAlertBoardAsync() => _http.GetFromJsonAsync<AlertBoardDto>("api/alerts");

    public async Task<ApiResult<TelemetryIngestResult>> SimulateCriticalAlertAsync(string kind)
    {
        using HttpResponseMessage response = await _http.PostAsync($"api/alerts/simulate?kind={Uri.EscapeDataString(kind)}", content: null);
        return await ApiResult<TelemetryIngestResult>.FromResponseAsync(response);
    }

    public Task<EngineSnapshotDto?> GetEngineAsync() => _http.GetFromJsonAsync<EngineSnapshotDto>("api/engine");

    public Task<LearnedRuleDto[]?> GetRulesAsync(int top) => _http.GetFromJsonAsync<LearnedRuleDto[]>($"api/engine/rules?top={top}");

    public async Task<ApiResult<SuggestionDto>> AcceptSuggestionAsync(string id)
    {
        using HttpResponseMessage response = await _http.PostAsync($"api/engine/suggestions/{Uri.EscapeDataString(id)}/accept", content: null);
        return await ApiResult<SuggestionDto>.FromResponseAsync(response);
    }

    public async Task<bool> DismissSuggestionAsync(string id)
    {
        using HttpResponseMessage response = await _http.PostAsync($"api/engine/suggestions/{Uri.EscapeDataString(id)}/dismiss", content: null);
        return response.IsSuccessStatusCode;
    }

    public Task<LookupBenchmarkDto?> RunLookupBenchmarkAsync(int devices, int lookups) =>
        _http.GetFromJsonAsync<LookupBenchmarkDto>($"api/diagnostics/lookup?devices={devices}&lookups={lookups}");

    private async Task<ApiResult<SimulatorStatusDto>> PostSimulatorAsync(string url)
    {
        using HttpResponseMessage response = await _http.PostAsync(url, content: null);
        return await ApiResult<SimulatorStatusDto>.FromResponseAsync(response);
    }
}
