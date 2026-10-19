using SmartX.Core.Commands;
using SmartX.Core.Contracts;
using SmartX.Core.Devices;

namespace SmartX.Api.Services;

/// <summary>A manual override command and the state changes it made.</summary>
public sealed class CommandRecord
{
    public required Guid Id { get; init; }
    public required CommandAction Action { get; init; }
    public required string Label { get; init; }
    public required string IssuedBy { get; init; }

    /// <summary>"Manual", "Suggestion", "Undo" or "Redo".</summary>
    public required string Source { get; init; }

    public string? SuggestionId { get; init; }

    /// <summary>For bulk commands: the Sub-Zone they were limited to, or "all".</summary>
    public string? Scope { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }
    public required IReadOnlyList<StateChange> Changes { get; init; }
    public Guid? RevertsId { get; init; }
    public string Status { get; set; } = "Pending";
    public DateTimeOffset? ConfirmedAt { get; set; }

    public CommandRecordDto ToDto() => new(
        Id,
        Action,
        Label,
        IssuedBy,
        Source,
        IssuedAt,
        Changes.Select(c => new CommandChangeDto(c.Mac.ToString(), c.DeviceName, c.Before, c.After)).ToArray(),
        Status,
        ConfirmedAt,
        ConfirmedAt is null ? (double?)null : Math.Round((ConfirmedAt.Value - IssuedAt).TotalMilliseconds, 0),
        RevertsId);
}

public readonly record struct StateChange(MacKey Mac, string DeviceName, bool Before, bool After);

/// <summary>
/// Manual override commands for actuators.
/// <list type="bullet">
/// <item>Device shadows in a <c>Dictionary&lt;MacKey, Shadow&gt;</c>: the desired state set by operators and the
/// state each device last reported. Devices fetch their desired state over HTTP, act on it, and report
/// back in telemetry, which confirms the command.</item>
/// <item>An <see cref="UndoRedoHistory{T}"/> built on two <see cref="Stack{T}"/>s: Undo pops the most recent
/// command and restores every device it touched, including bulk commands such as "close all valves".</item>
/// </list>
/// </summary>
public sealed class CommandService
{
    private const int RecentCapacity = 40;
    private const string ValveMetric = "valve_state";

    private readonly object _gate = new();
    private readonly Dictionary<MacKey, Shadow> _shadows = new();
    private readonly UndoRedoHistory<CommandRecord> _history = new(50);
    private readonly Queue<CommandRecord> _recent = new();
    private readonly DeviceRegistry _registry;
    private readonly TimelineService _timeline;
    private readonly TimeProvider _time;
    private long _version;

    public CommandService(DeviceRegistry registry, TimelineService timeline, TimeProvider time)
    {
        _registry = registry;
        _timeline = timeline;
        _time = time;
    }

    /// <summary>Raised after an operator command (not undo / redo) is applied, outside the lock.</summary>
    public event Action<CommandRecord>? Executed;

    public long Version => Interlocked.Read(ref _version);

    /// <summary>Starts tracking an actuator with its power-on default state.</summary>
    public void Track(SensorDevice device)
    {
        if (!device.IsActuator)
        {
            return;
        }

        lock (_gate)
        {
            _shadows.TryAdd(device.Key, new Shadow(device, DefaultState(device)) { DesiredSince = _time.GetUtcNow() });
        }
    }

    public void SetInitialState(MacKey mac, bool desired)
    {
        lock (_gate)
        {
            if (_shadows.TryGetValue(mac, out Shadow? shadow))
            {
                shadow.Desired = desired;
                shadow.DesiredSince = _time.GetUtcNow();
            }
        }
    }

    public bool? DesiredState(MacKey mac)
    {
        lock (_gate)
        {
            return _shadows.TryGetValue(mac, out Shadow? shadow) ? shadow.Desired : null;
        }
    }

    public CommandResultDto Execute(IssueCommandRequest request)
    {
        DateTimeOffset now = _time.GetUtcNow();
        string issuedBy = string.IsNullOrWhiteSpace(request.IssuedBy) ? "operator" : request.IssuedBy.Trim();
        string source = string.IsNullOrWhiteSpace(request.SuggestionId) ? "Manual" : "Suggestion";
        CommandRecord record;

        lock (_gate)
        {
            List<StateChange> changes;
            string label;
            string? scope = null;

            switch (request.Action)
            {
                case CommandAction.TurnOn:
                case CommandAction.TurnOff:
                case CommandAction.Toggle:
                {
                    if (!_registry.TryGet(request.DeviceMac, out SensorDevice? device))
                    {
                        return Fail($"Device '{request.DeviceMac}' is not registered.");
                    }

                    if (!_shadows.TryGetValue(device.Key, out Shadow? shadow))
                    {
                        return Fail($"{device.DisplayName} is a {device.Category} sensor; only actuators accept commands.");
                    }

                    bool after = request.Action switch
                    {
                        CommandAction.TurnOn => true,
                        CommandAction.TurnOff => false,
                        _ => !shadow.Desired
                    };

                    if (after == shadow.Desired)
                    {
                        return Fail($"{device.DisplayName} is already {StateWord(device, after)}.");
                    }

                    changes = new List<StateChange> { new(device.Key, device.DisplayName, shadow.Desired, after) };
                    label = $"{Verb(request.Action, device)} {device.DisplayName}";
                    break;
                }

                case CommandAction.CloseAllValves:
                case CommandAction.OpenAllValves:
                {
                    bool target = request.Action == CommandAction.OpenAllValves;
                    changes = new List<StateChange>();
                    foreach (Shadow shadow in _shadows.Values)
                    {
                        if (shadow.Device.Metric != ValveMetric || shadow.Desired == target)
                        {
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(request.SubZoneId) &&
                            !string.Equals(shadow.Device.Location.SubZoneId, request.SubZoneId, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        changes.Add(new StateChange(shadow.Device.Key, shadow.Device.DisplayName, shadow.Desired, target));
                    }

                    if (changes.Count == 0)
                    {
                        return Fail($"Every valve in scope is already {(target ? "open" : "closed")}.");
                    }

                    scope = string.IsNullOrWhiteSpace(request.SubZoneId) ? "all" : request.SubZoneId.Trim().ToLowerInvariant();
                    label = $"{(target ? "Open" : "Close")} all valves ({changes.Count} valve(s), {(scope == "all" ? "all Sub-Zones" : scope)})";
                    break;
                }

                default:
                    return Fail($"Unknown command '{request.Action}'.");
            }

            record = new CommandRecord
            {
                Id = Guid.NewGuid(),
                Action = request.Action,
                Label = label,
                IssuedBy = issuedBy,
                Source = source,
                SuggestionId = request.SuggestionId,
                Scope = scope,
                IssuedAt = now,
                Changes = changes
            };

            Apply(changes, useAfter: true, now);
            _history.Record(record);
            AddRecent(record);
        }

        _timeline.AddHistory(HistoryKind.Command, record.Label, $"by {record.IssuedBy} ({record.Source})", record.Changes.Count == 1 ? record.Changes[0].Mac.ToString() : null, now);
        Executed?.Invoke(record);
        return Ok($"{record.Label}: sent to {record.Changes.Count} device(s).", record);
    }

    /// <summary>Pops the last command off the undo stack and restores every device it changed.</summary>
    public CommandResultDto Undo(string? issuedBy)
    {
        DateTimeOffset now = _time.GetUtcNow();
        CommandRecord revert;

        lock (_gate)
        {
            if (!_history.TryUndo(out CommandRecord? original))
            {
                return Fail("Nothing to undo.");
            }

            List<StateChange> reversed = original.Changes
                .Select(c => new StateChange(c.Mac, c.DeviceName, c.After, c.Before))
                .ToList();

            Apply(reversed, useAfter: true, now);
            revert = new CommandRecord
            {
                Id = Guid.NewGuid(),
                Action = original.Action,
                Label = $"Undo: {original.Label}",
                IssuedBy = string.IsNullOrWhiteSpace(issuedBy) ? "operator" : issuedBy.Trim(),
                Source = "Undo",
                IssuedAt = now,
                Changes = reversed,
                RevertsId = original.Id
            };
            AddRecent(revert);
        }

        _timeline.AddHistory(HistoryKind.Undo, revert.Label, $"{revert.Changes.Count} device(s) restored", null, now);
        return Ok($"{revert.Label}: {revert.Changes.Count} device(s) restored.", revert);
    }

    public CommandResultDto Redo(string? issuedBy)
    {
        DateTimeOffset now = _time.GetUtcNow();
        CommandRecord again;

        lock (_gate)
        {
            if (!_history.TryRedo(out CommandRecord? original))
            {
                return Fail("Nothing to redo.");
            }

            Apply(original.Changes, useAfter: true, now);
            again = new CommandRecord
            {
                Id = Guid.NewGuid(),
                Action = original.Action,
                Label = $"Redo: {original.Label}",
                IssuedBy = string.IsNullOrWhiteSpace(issuedBy) ? "operator" : issuedBy.Trim(),
                Source = "Redo",
                IssuedAt = now,
                Changes = original.Changes,
                RevertsId = original.Id
            };
            AddRecent(again);
        }

        _timeline.AddHistory(HistoryKind.Redo, again.Label, $"{again.Changes.Count} device(s) changed", null, now);
        return Ok($"{again.Label}.", again);
    }

    /// <summary>Called when an actuator's telemetry arrives: updates the shadow and confirms pending commands.</summary>
    public void OnReported(MacKey mac, bool state, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_shadows.TryGetValue(mac, out Shadow? shadow))
            {
                return;
            }

            shadow.Reported = state;
            shadow.ReportedAt = at;

            bool changed = false;
            foreach (CommandRecord record in _recent)
            {
                if (record.Status != "Pending" || record.Changes.All(c => c.Mac != mac))
                {
                    continue;
                }

                bool allConfirmed = record.Changes.All(c =>
                    _shadows.TryGetValue(c.Mac, out Shadow? s) && s.Reported == c.After && s.ReportedAt >= record.IssuedAt);

                if (allConfirmed)
                {
                    record.Status = "Confirmed";
                    record.ConfirmedAt = at;
                    changed = true;
                }
            }

            if (changed)
            {
                _version++;
            }
        }
    }

    public ActuatorShadowDto[] Shadows()
    {
        lock (_gate)
        {
            return _shadows.Values
                .OrderBy(s => s.Device.Location.SubZoneId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Device.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(s => new ActuatorShadowDto(
                    s.Device.MacAddress,
                    s.Device.DisplayName,
                    s.Device.Metric,
                    s.Device.Location.SubZoneId,
                    s.Desired,
                    s.Reported,
                    s.ReportedAt,
                    s.Reported == s.Desired))
                .ToArray();
        }
    }

    /// <summary>Actuators whose device has not adopted the desired state for longer than <paramref name="grace"/>.</summary>
    public List<(SensorDevice Device, bool Desired, bool? Reported, TimeSpan Waiting)> OutOfSync(TimeSpan grace)
    {
        DateTimeOffset now = _time.GetUtcNow();
        var result = new List<(SensorDevice, bool, bool?, TimeSpan)>();
        lock (_gate)
        {
            foreach (Shadow shadow in _shadows.Values)
            {
                TimeSpan waiting = now - shadow.DesiredSince;
                if (shadow.Reported != shadow.Desired && waiting > grace)
                {
                    result.Add((shadow.Device, shadow.Desired, shadow.Reported, waiting));
                }
            }
        }

        return result;
    }

    public CommandHistoryDto History()
    {
        lock (_gate)
        {
            return new CommandHistoryDto(
                _history.UndoSnapshot().Select(r => r.ToDto()).ToArray(),
                _history.RedoSnapshot().Select(r => r.ToDto()).ToArray(),
                _recent.Reverse().Select(r => r.ToDto()).ToArray(),
                _history.Capacity);
        }
    }

    private void Apply(IEnumerable<StateChange> changes, bool useAfter, DateTimeOffset now)
    {
        foreach (StateChange change in changes)
        {
            if (_shadows.TryGetValue(change.Mac, out Shadow? shadow))
            {
                shadow.Desired = useAfter ? change.After : change.Before;
                shadow.DesiredSince = now;
            }
        }

        _version++;
    }

    private void AddRecent(CommandRecord record)
    {
        _recent.Enqueue(record);
        while (_recent.Count > RecentCapacity)
        {
            _recent.Dequeue();
        }

        _version++;
    }

    private CommandResultDto Ok(string message, CommandRecord record) => new(true, message, record.ToDto(), History());

    private CommandResultDto Fail(string message) => new(false, message, null, History());

    private static bool DefaultState(SensorDevice device) => device.Metric switch
    {
        "pump_relay" => false,
        _ => true // valves open and switches on at power-up
    };

    public static string Verb(CommandAction action, SensorDevice device) => action switch
    {
        CommandAction.TurnOn => device.Metric == ValveMetric ? "Open" : "Turn on",
        CommandAction.TurnOff => device.Metric == ValveMetric ? "Close" : "Turn off",
        CommandAction.Toggle => "Toggle",
        CommandAction.CloseAllValves => "Close all valves",
        _ => "Open all valves"
    };

    public static string StateWord(SensorDevice device, bool state) =>
        device.Metric == ValveMetric ? (state ? "open" : "closed") : (state ? "on" : "off");

    private sealed class Shadow
    {
        public Shadow(SensorDevice device, bool desired)
        {
            Device = device;
            Desired = desired;
        }

        public SensorDevice Device { get; }
        public bool Desired { get; set; }
        public DateTimeOffset DesiredSince { get; set; }
        public bool? Reported { get; set; }
        public DateTimeOffset? ReportedAt { get; set; }
    }
}
