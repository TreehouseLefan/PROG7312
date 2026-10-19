using System.Collections.Concurrent;
using System.Globalization;
using SmartX.Core.Analytics;
using SmartX.Core.Contracts;
using SmartX.Core.Pulse;
using SmartX.Core.Streaming;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Predictive Action and Recommendation Engine ("Automated Action Engine").
/// <para><b>Learning.</b> Context events (alerts such as "soil moisture in Sub-Zone B below 20%") and operator
/// actions (searches, device inspections, commands) flow into a <see cref="PatternMiner"/>, which learns
/// context → action association rules and action → action sequences, with recency weighting.</para>
/// <para><b>Suggesting.</b> Every second the engine looks at the alerts that are active right now, ranks the
/// actions operators historically took in that situation, removes ones that are already done or were
/// dismissed, and publishes the top five as "Suggested Actions" with the evidence behind each.</para>
/// <para><b>Anticipating.</b> Independently of history, it fits a least-squares trend to every critical metric
/// and flags devices projected to cross a critical threshold soon, plus faulty, flapping, unconfirmed and
/// clustered-outage devices, as "Automated Insights".</para>
/// <para>Events arrive on other threads (packet processing, HTTP requests). They are put on a lock-free
/// <see cref="ConcurrentQueue{T}"/> inbox and applied in order at the start of each refresh, so the engine
/// never takes its lock while another component holds its own (no lock-order deadlocks).</para>
/// </summary>
public sealed class ActionEngine
{
    private const int MaxSuggestions = 5;
    private const int MaxInsights = 12;
    private static readonly TimeSpan DismissFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TrendWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TrendHorizon = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly PatternMiner _miner = new();
    private readonly ConcurrentQueue<EngineEvent> _inbox = new();
    private readonly Dictionary<string, SuggestionDto> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _dismissedUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _debounce = new(StringComparer.Ordinal);
    private readonly DeviceRegistry _registry;
    private readonly AlertCenter _alerts;
    private readonly CommandService _commands;
    private readonly TimelineService _timeline;
    private readonly TopologyService _topology;
    private readonly TelemetryIngestionService _ingestion;
    private readonly TimeProvider _time;

    private EngineSnapshotDto _snapshot;
    private string _fingerprint = string.Empty;
    private long _version;
    private long _shown;
    private long _accepted;
    private long _anticipated;
    private long _liveActions;

    public ActionEngine(
        DeviceRegistry registry,
        AlertCenter alerts,
        CommandService commands,
        TimelineService timeline,
        TopologyService topology,
        TelemetryIngestionService ingestion,
        TimeProvider time)
    {
        _registry = registry;
        _alerts = alerts;
        _commands = commands;
        _timeline = timeline;
        _topology = topology;
        _ingestion = ingestion;
        _time = time;
        _snapshot = new EngineSnapshotDto(time.GetUtcNow(), Array.Empty<SuggestionDto>(), Array.Empty<InsightDto>(), EmptyStats());

        _alerts.AlertRaised += OnAlertRaised;
        _commands.Executed += OnCommandExecuted;
    }

    public long Version => Interlocked.Read(ref _version);

    public EngineSnapshotDto Snapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    // ------------------------------------------------------------------
    // Inputs
    // ------------------------------------------------------------------

    private void OnAlertRaised(AlertDto alert)
    {
        // Two granularities: the Sub-Zone (generalises to any sensor there) and the device itself.
        _inbox.Enqueue(new EngineEvent(false, $"{alert.Type}@{alert.SubZoneId}", alert.RaisedAt, null));
        _inbox.Enqueue(new EngineEvent(false, $"{alert.Type}@{alert.Mac}", alert.RaisedAt, null));
    }

    private void OnCommandExecuted(CommandRecord record)
    {
        string? key = ActionKeyFor(record);
        if (key is not null)
        {
            _inbox.Enqueue(new EngineEvent(true, key, record.IssuedAt, record.SuggestionId));
        }
    }

    /// <summary>Records a committed search (Enter / Search button), e.g. "pump a".</summary>
    public void ObserveSearch(string? query, DateTimeOffset at)
    {
        string normalized = NormalizeQuery(query);
        if (normalized.Length < 2 || IsDebounced("search:" + normalized, at, TimeSpan.FromSeconds(5)))
        {
            return;
        }

        _inbox.Enqueue(new EngineEvent(true, "Search:" + normalized, at, null));
        _timeline.AddHistory(HistoryKind.Search, $"Search “{normalized}”", "Operator search recorded for pattern analysis.", null, at);
    }

    /// <summary>Records that an operator opened a device in the Command Stream.</summary>
    public void ObserveInspect(SensorDevice device, DateTimeOffset at)
    {
        if (IsDebounced("inspect:" + device.MacAddress, at, TimeSpan.FromSeconds(10)))
        {
            return;
        }

        _inbox.Enqueue(new EngineEvent(true, "Inspect:" + device.MacAddress, at, null));
    }

    /// <summary>Accepts a non-command suggestion (Inspect / Search). Command suggestions go through POST /api/commands.</summary>
    public SuggestionDto? Accept(string id, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_current.TryGetValue(id, out SuggestionDto? suggestion))
            {
                return null;
            }

            _inbox.Enqueue(new EngineEvent(true, suggestion.ActionKey, at, id));
            _timeline.AddHistory(HistoryKind.Suggestion, $"Accepted: {suggestion.Title}", suggestion.Reason, suggestion.DeviceMac, at);
            return suggestion;
        }
    }

    public bool Dismiss(string id, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_current.Remove(id, out SuggestionDto? suggestion))
            {
                return false;
            }

            _dismissedUntil[id] = at + DismissFor;
            _snapshot = _snapshot with { Suggestions = _current.Values.ToArray() };
            _version++;
            _timeline.AddHistory(HistoryKind.Suggestion, $"Dismissed: {suggestion.Title}", "Hidden for 10 minutes.", suggestion.DeviceMac, at);
            return true;
        }
    }

    /// <summary>Learns from historical (seeded) operator sessions. Not counted in live accuracy statistics.</summary>
    public void LearnHistory(IEnumerable<(DateTimeOffset At, bool IsAction, string Key)> events)
    {
        lock (_gate)
        {
            foreach ((DateTimeOffset at, bool isAction, string key) in events.OrderBy(e => e.At))
            {
                if (isAction)
                {
                    _miner.ObserveAction(key, at);
                }
                else
                {
                    _miner.ObserveContext(key, at);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Refresh
    // ------------------------------------------------------------------

    /// <summary>Recomputes suggestions and insights. Returns true when the result changed.</summary>
    public bool Refresh()
    {
        DateTimeOffset now = _time.GetUtcNow();

        // Gather inputs before taking the engine lock.
        AlertDto[] active = _alerts.Active();
        Dictionary<string, bool> desired = _commands.Shadows().ToDictionary(s => s.Mac, s => s.Desired, StringComparer.OrdinalIgnoreCase);
        List<InsightDto> insights = BuildInsights(now, active);

        lock (_gate)
        {
            DrainInbox();

            var contexts = new HashSet<string>(StringComparer.Ordinal);
            foreach (AlertDto alert in active)
            {
                contexts.Add($"{alert.Type}@{alert.SubZoneId}");
                contexts.Add($"{alert.Type}@{alert.Mac}");
            }

            foreach (string expired in _dismissedUntil.Where(d => d.Value <= now).Select(d => d.Key).ToList())
            {
                _dismissedUntil.Remove(expired);
            }

            var suggestions = new List<SuggestionDto>();
            foreach (MinedRecommendation recommendation in _miner.Recommend(contexts, now, MaxSuggestions * 3))
            {
                SuggestionDto? suggestion = ToSuggestion(recommendation, desired);
                if (suggestion is null || _dismissedUntil.ContainsKey(suggestion.Id))
                {
                    continue;
                }

                suggestions.Add(suggestion);
                if (suggestions.Count == MaxSuggestions)
                {
                    break;
                }
            }

            foreach (SuggestionDto suggestion in suggestions)
            {
                if (!_current.ContainsKey(suggestion.Id))
                {
                    _shown++;
                }
            }

            _current.Clear();
            foreach (SuggestionDto suggestion in suggestions)
            {
                _current[suggestion.Id] = suggestion;
            }

            string fingerprint = string.Join('|', suggestions.Select(s => $"{s.Id}:{s.Confidence:0.00}"))
                                 + "#" + string.Join('|', insights.Select(i => $"{i.Id}:{i.Title}"));

            _snapshot = new EngineSnapshotDto(now, suggestions.ToArray(), insights.ToArray(), Stats());
            if (fingerprint == _fingerprint)
            {
                return false;
            }

            _fingerprint = fingerprint;
            _version++;
            return true;
        }
    }

    public LearnedRuleDto[] Rules(int top)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            DrainInbox();
            return _miner.Rules(now, top)
                .Select(r => new LearnedRuleDto(
                    r.Context,
                    r.Action,
                    $"When {DescribeContext(r.Context)} → operators {DescribeAction(r.Action)}",
                    r.Support,
                    r.Confidence,
                    r.Lift))
                .ToArray();
        }
    }

    private void DrainInbox()
    {
        while (_inbox.TryDequeue(out EngineEvent item))
        {
            if (!item.IsAction)
            {
                _miner.ObserveContext(item.Key, item.At);
                continue;
            }

            _liveActions++;
            if (_current.Values.Any(s => s.ActionKey == item.Key))
            {
                _anticipated++; // the engine had already suggested what the operator did
            }

            if (item.SuggestionId is not null && _current.ContainsKey(item.SuggestionId))
            {
                _accepted++;
            }

            _miner.ObserveAction(item.Key, item.At);
        }
    }

    private EngineStatsDto Stats() => new(
        _miner.ContextsObserved,
        _miner.ActionsObserved,
        _miner.RuleCount,
        _shown,
        _accepted,
        _anticipated,
        _liveActions == 0 ? 0 : Math.Round((double)_anticipated / _liveActions, 3),
        _shown == 0 ? 0 : Math.Round((double)_accepted / _shown, 3));

    private static EngineStatsDto EmptyStats() => new(0, 0, 0, 0, 0, 0, 0, 0);

    // ------------------------------------------------------------------
    // Suggestions
    // ------------------------------------------------------------------

    private SuggestionDto? ToSuggestion(MinedRecommendation recommendation, Dictionary<string, bool> desired)
    {
        string key = recommendation.Action;
        string reason = BuildReason(recommendation);
        double confidence = Math.Round(recommendation.Confidence, 3);
        double support = Math.Round(recommendation.Support, 1);
        string id = StableId(key);

        if (key.StartsWith("Cmd:", StringComparison.Ordinal))
        {
            string rest = key[4..];
            int colon = rest.IndexOf(':');
            if (colon < 0 || !Enum.TryParse(rest[..colon], out CommandAction action))
            {
                return null;
            }

            string target = rest[(colon + 1)..];

            if (action is CommandAction.CloseAllValves or CommandAction.OpenAllValves)
            {
                bool wanted = action == CommandAction.OpenAllValves;
                bool anyToChange = _commands.Shadows().Any(s =>
                    s.Metric == "valve_state" &&
                    s.Desired != wanted &&
                    (target == "all" || string.Equals(s.SubZoneId, target, StringComparison.OrdinalIgnoreCase)));
                if (!anyToChange)
                {
                    return null;
                }

                string scopeName = target == "all" ? "every Sub-Zone" : SubZoneName(target);
                return new SuggestionDto(id, $"{(wanted ? "Open" : "Close")} all valves in {scopeName}", reason, confidence, support,
                    recommendation.Source, key, "Command", null, null, action, null, recommendation.Triggers);
            }

            if (!_registry.TryGet(target, out SensorDevice? device))
            {
                return null;
            }

            if (desired.TryGetValue(device.MacAddress, out bool isOn) &&
                ((action == CommandAction.TurnOn && isOn) || (action == CommandAction.TurnOff && !isOn)))
            {
                return null; // already in the suggested state
            }

            return new SuggestionDto(id, $"{CommandService.Verb(action, device)} {device.DisplayName}", reason, confidence, support,
                recommendation.Source, key, "Command", device.MacAddress, device.DisplayName, action, null, recommendation.Triggers);
        }

        if (key.StartsWith("Inspect:", StringComparison.Ordinal))
        {
            if (!_registry.TryGet(key["Inspect:".Length..], out SensorDevice? device))
            {
                return null;
            }

            return new SuggestionDto(id, $"Inspect {device.DisplayName}", reason, confidence, support,
                recommendation.Source, key, "Inspect", device.MacAddress, device.DisplayName, null, null, recommendation.Triggers);
        }

        if (key.StartsWith("Search:", StringComparison.Ordinal))
        {
            string query = key["Search:".Length..];
            return new SuggestionDto(id, $"Search “{query}”", reason, confidence, support,
                recommendation.Source, key, "Search", null, null, null, query, recommendation.Triggers);
        }

        return null;
    }

    private string BuildReason(MinedRecommendation recommendation)
    {
        string percent = (recommendation.Confidence * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        string weight = recommendation.Support.ToString("0.#", CultureInfo.InvariantCulture);

        List<string> situations = recommendation.Triggers.Where(t => !t.StartsWith("after:", StringComparison.Ordinal)).ToList();
        string? sequence = recommendation.Triggers.FirstOrDefault(t => t.StartsWith("after:", StringComparison.Ordinal));
        string? previous = sequence is null ? null : sequence.Substring("after:".Length);

        if (recommendation.Source == "Sequence" || situations.Count == 0)
        {
            return $"After operators {DescribeAction(previous ?? string.Empty)}, they next {DescribeAction(recommendation.Action)} {percent} of the time (≈{weight} recency-weighted sequences).";
        }

        string triggers = string.Join(" and ", situations.Select(DescribeContext).Distinct());
        string lift = recommendation.Lift.ToString("0.0", CultureInfo.InvariantCulture);
        string reason = $"When {triggers}, operators {DescribeAction(recommendation.Action)} within 10 min in {percent} of past cases " +
                        $"(≈{weight} recency-weighted occurrences, lift ×{lift}).";

        return previous is null
            ? reason
            : reason + $" It is also the usual next step after operators {DescribeAction(previous)}.";
    }

    private string DescribeContext(string context)
    {
        int at = context.IndexOf('@');
        if (at < 0)
        {
            return context;
        }

        string type = context[..at];
        string target = context[(at + 1)..];
        string what = type switch
        {
            nameof(AlertType.CriticalMoisture) => "soil moisture fell below the critical level",
            nameof(AlertType.PowerSurge) => "a severe power spike occurred",
            nameof(AlertType.Disconnected) => "a device disconnected",
            nameof(AlertType.Spike) => "a sensor broke its baseline",
            nameof(AlertType.Chatter) => "an actuator started chattering",
            nameof(AlertType.OutOfRange) => "out-of-range readings arrived",
            _ => type
        };

        if (_registry.TryGet(target, out SensorDevice? device))
        {
            return $"{what} on {device.DisplayName}";
        }

        return $"{what} in {SubZoneName(target)}";
    }

    private string DescribeAction(string action)
    {
        if (action.StartsWith("Search:", StringComparison.Ordinal))
        {
            return $"searched “{action["Search:".Length..]}”";
        }

        if (action.StartsWith("Inspect:", StringComparison.Ordinal))
        {
            return _registry.TryGet(action["Inspect:".Length..], out SensorDevice? inspected)
                ? $"inspected {inspected.DisplayName}"
                : "inspected a device";
        }

        if (action.StartsWith("Cmd:", StringComparison.Ordinal))
        {
            string rest = action[4..];
            int colon = rest.IndexOf(':');
            if (colon > 0 && Enum.TryParse(rest[..colon], out CommandAction command))
            {
                string target = rest[(colon + 1)..];
                if (command is CommandAction.CloseAllValves or CommandAction.OpenAllValves)
                {
                    string scope = target == "all" ? "every Sub-Zone" : SubZoneName(target);
                    return $"{(command == CommandAction.OpenAllValves ? "opened" : "closed")} all valves in {scope}";
                }

                if (_registry.TryGet(target, out SensorDevice? device))
                {
                    bool valve = device.Metric == "valve_state";
                    string past = command switch
                    {
                        CommandAction.TurnOn => valve ? "opened" : "turned on",
                        CommandAction.TurnOff => valve ? "closed" : "turned off",
                        _ => "toggled"
                    };
                    return $"{past} {device.DisplayName}";
                }
            }
        }

        return action;
    }

    private string SubZoneName(string subZoneId) =>
        _topology.TryGetSubZone(subZoneId, out GridGroupDto? group) ? group.SubZoneName : subZoneId;

    /// <summary>Command keys are normalised so that "Toggle" is learned as the on/off it actually caused.</summary>
    public static string? ActionKeyFor(CommandRecord record)
    {
        if (record.Source is "Undo" or "Redo")
        {
            return null;
        }

        if (record.Action is CommandAction.CloseAllValves or CommandAction.OpenAllValves)
        {
            return $"Cmd:{record.Action}:{record.Scope ?? "all"}";
        }

        if (record.Changes.Count != 1)
        {
            return null;
        }

        StateChange change = record.Changes[0];
        CommandAction normalised = change.After ? CommandAction.TurnOn : CommandAction.TurnOff;
        return $"Cmd:{normalised}:{change.Mac}";
    }

    public static string NormalizeQuery(string? query) =>
        string.Join(' ', DeviceRegistry.Tokenize(query));

    private static string StableId(string key)
    {
        // FNV-1a 64-bit: stable across restarts, unlike string.GetHashCode().
        ulong hash = 14695981039346656037UL;
        foreach (char c in key)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return "s" + hash.ToString("x12", CultureInfo.InvariantCulture)[..12];
    }

    private bool IsDebounced(string key, DateTimeOffset at, TimeSpan window)
    {
        lock (_debounce)
        {
            if (_debounce.TryGetValue(key, out DateTimeOffset last) && at - last < window)
            {
                return true;
            }

            _debounce[key] = at;
            if (_debounce.Count > 500)
            {
                foreach (string old in _debounce.Where(d => at - d.Value > TimeSpan.FromMinutes(5)).Select(d => d.Key).ToList())
                {
                    _debounce.Remove(old);
                }
            }

            return false;
        }
    }

    // ------------------------------------------------------------------
    // Insights
    // ------------------------------------------------------------------

    private List<InsightDto> BuildInsights(DateTimeOffset now, AlertDto[] active)
    {
        var insights = new List<InsightDto>();
        AlertOptions thresholds = _alerts.Options;

        foreach (AlertDto alert in active.Where(a => a.Severity == AlertSeverity.Critical))
        {
            insights.Add(new InsightDto(
                $"alert:{alert.Key}",
                "CriticalAlert",
                "Critical",
                alert.Message,
                "Processed on the priority lane ahead of standard telemetry.",
                alert.Mac,
                null));
        }

        foreach (SensorDevice device in _registry.All())
        {
            bool moisture = device.Metric == "soil_moisture";
            bool power = device.Metric == "power_draw";

            if (moisture || power)
            {
                InsightDto? trend = ProjectTrend(device, now, moisture, moisture ? thresholds.MoistureCriticalBelow : thresholds.PowerCriticalAbove);
                if (trend is not null)
                {
                    insights.Add(trend);
                }
            }

            int rejections = _ingestion.RecentRejections(device.Key, now.AddMinutes(-10));
            if (rejections >= 3)
            {
                insights.Add(new InsightDto(
                    $"fault:{device.MacAddress}",
                    "SensorFault",
                    "Warning",
                    $"{device.DisplayName} may be faulty or mis-calibrated",
                    $"{rejections} out-of-range readings in the last 10 minutes were rejected by the gateway.",
                    device.MacAddress,
                    null));
            }

            int disconnects = _alerts.RecentDisconnects(device.Key, now.AddMinutes(-15));
            if (disconnects >= 3)
            {
                insights.Add(new InsightDto(
                    $"flap:{device.MacAddress}",
                    "UnstableLink",
                    "Warning",
                    $"{device.DisplayName} keeps dropping off the network",
                    $"{disconnects} separate disconnects in 15 minutes: check its power supply, antenna or signal strength.",
                    device.MacAddress,
                    null));
            }
        }

        AlertBoardDto board = _alerts.Board();
        foreach (IGrouping<string, string> group in board.DisconnectedMacs
                     .Select(mac => (Mac: mac, Device: _registry.TryGet(mac, out SensorDevice? d) ? d : null))
                     .Where(x => x.Device is not null)
                     .GroupBy(x => x.Device!.Location.SubZoneId, x => x.Mac))
        {
            int total = _registry.InSubZone(group.Key).Count;
            int silent = group.Count();
            if (total >= 2 && silent * 10 >= total * 6)
            {
                insights.Add(new InsightDto(
                    $"cluster:{group.Key}",
                    "SharedFault",
                    "Critical",
                    $"Shared outage suspected in {SubZoneName(group.Key)}",
                    $"{silent} of {total} devices are disconnected at once: a gateway, power or network fault is more likely than {silent} separate device failures.",
                    null,
                    null));
            }
        }

        foreach ((SensorDevice device, bool wanted, bool? reported, TimeSpan waiting) in _commands.OutOfSync(TimeSpan.FromSeconds(15)))
        {
            insights.Add(new InsightDto(
                $"sync:{device.MacAddress}",
                "CommandNotConfirmed",
                "Warning",
                $"{device.DisplayName} has not confirmed its last command",
                $"Desired {CommandService.StateWord(device, wanted)}, last reported {(reported is null ? "nothing" : CommandService.StateWord(device, reported.Value))} for {waiting.TotalSeconds:0} s.",
                device.MacAddress,
                null));
        }

        return insights
            .OrderBy(i => SeverityRank(i.Severity))
            .ThenBy(i => i.ProjectedSeconds ?? double.MaxValue)
            .Take(MaxInsights)
            .ToList();
    }

    private InsightDto? ProjectTrend(SensorDevice device, DateTimeOffset now, bool falling, double threshold)
    {
        AlertType criticalType = falling ? AlertType.CriticalMoisture : AlertType.PowerSurge;
        if (_alerts.IsActive(device.Key, criticalType) || _alerts.IsDisconnected(device.Key))
        {
            return null;
        }

        List<(DateTimeOffset Timestamp, double Value)> points = _timeline.Range(device.Key, now - TrendWindow, now);
        if (points.Count < 12)
        {
            return null;
        }

        var x = new double[points.Count];
        var y = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            x[i] = (points[i].Timestamp - now).TotalSeconds;
            y[i] = points[i].Value;
        }

        TrendFit? fit = LinearTrend.Fit(x, y);
        if (fit is not TrendFit line || line.RSquared < 0.4)
        {
            return null;
        }

        double current = line.Predict(0);
        bool heading = falling ? line.Slope < 0 && current > threshold : line.Slope > 0 && current < threshold;
        if (!heading)
        {
            return null;
        }

        double? seconds = line.SecondsUntil(threshold, 0);
        if (seconds is not double eta || eta > TrendHorizon.TotalSeconds)
        {
            return null;
        }

        string unit = device.Unit;
        string rate = (line.Slope * 60).ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
        string minutes = Math.Max(1, eta / 60).ToString("0", CultureInfo.InvariantCulture);
        string direction = falling ? "fall below" : "exceed";

        return new InsightDto(
            $"trend:{device.MacAddress}",
            "Forecast",
            eta < 300 ? "Warning" : "Info",
            $"{device.DisplayName} projected to {direction} {threshold:0.##}{unit} in ~{minutes} min",
            $"Least-squares trend {rate} {unit}/min over the last {TrendWindow.TotalMinutes:0} min (R² {line.RSquared:0.00}, n={line.Count}). Act before the critical alert fires.",
            device.MacAddress,
            Math.Round(eta, 0));
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "Critical" => 0,
        "Warning" => 1,
        _ => 2
    };

    private readonly record struct EngineEvent(bool IsAction, string Key, DateTimeOffset At, string? SuggestionId);
}
