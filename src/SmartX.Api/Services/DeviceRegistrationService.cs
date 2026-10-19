using System.ComponentModel.DataAnnotations;
using SmartX.Core.Contracts;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;
using SmartX.Core.Topology;

namespace SmartX.Api.Services;

/// <summary>
/// Registers a sensor: validates the request, attaches a node to the topology tree, and then runs the
/// recursive placement check (Node -> Sub-Zone -> Zone -> Facility). Unsafe placements are rolled back.
/// </summary>
public sealed class DeviceRegistrationService
{
    private readonly object _gate = new();
    private readonly DeviceRegistry _registry;
    private readonly TopologyService _topology;
    private readonly PulseMonitor _pulse;
    private readonly CommandService _commands;
    private readonly TimeProvider _time;

    public DeviceRegistrationService(
        DeviceRegistry registry,
        TopologyService topology,
        PulseMonitor pulse,
        CommandService commands,
        TimeProvider time)
    {
        _registry = registry;
        _topology = topology;
        _pulse = pulse;
        _commands = commands;
        _time = time;
    }

    public GatewayOutcome<SensorDevice> Register(RegisterDeviceRequest request)
    {
        if (request is null)
        {
            return GatewayOutcome.Invalid<SensorDevice>("request", "A request body is required.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var annotationResults = new List<ValidationResult>();

        if (!Validator.TryValidateObject(request, new ValidationContext(request), annotationResults, validateAllProperties: true))
        {
            foreach (ValidationResult result in annotationResults)
            {
                string[] members = result.MemberNames.Any() ? result.MemberNames.ToArray() : new[] { "request" };
                foreach (string member in members)
                {
                    AddError(errors, member, result.ErrorMessage ?? "Invalid value.");
                }
            }
        }

        if (!MacAddress.TryNormalize(request.MacAddress, out string mac))
        {
            AddError(errors, nameof(request.MacAddress), "Use six hex pairs, e.g. 24:0A:C4:12:34:56.");
        }

        if (request.Category == SensorCategory.Actuator)
        {
            if (request.MinValue is not null || request.MaxValue is not null)
            {
                AddError(errors, nameof(request.MinValue), "Actuators report on/off states, so leave the value range empty.");
            }
        }
        else if (request.MinValue is double min && request.MaxValue is double max && min >= max)
        {
            AddError(errors, nameof(request.MinValue), "The minimum must be below the maximum.");
        }

        if (errors.Count > 0)
        {
            return GatewayOutcome.Invalid<SensorDevice>(errors);
        }

        lock (_gate)
        {
            if (_registry.Contains(mac))
            {
                return GatewayOutcome.Invalid<SensorDevice>(nameof(request.MacAddress), $"{mac} is already registered.");
            }

            string nodeId = $"node-{MacAddress.ToSlug(mac)}";
            var node = new DeploymentNode
            {
                Id = nodeId,
                Name = request.DisplayName.Trim(),
                Level = DeploymentLevel.Node,
                DeviceMac = mac,
                Profile = request.MinValue is null && request.MaxValue is null
                    ? null
                    : new ConfigProfile { MinValue = request.MinValue, MaxValue = request.MaxValue }
            };

            if (!_topology.TryAttachDevice(request.SubZoneId, node, request.Room, out DeploymentLocationDto? location, out string attachError))
            {
                return GatewayOutcome.Invalid<SensorDevice>(nameof(request.SubZoneId), attachError);
            }

            // Recursive placement check with the new device's reporting interval.
            int newInterval = request.ExpectedIntervalSeconds;
            var validator = new DeploymentTreeValidator(candidate =>
                string.Equals(candidate, mac, StringComparison.OrdinalIgnoreCase)
                    ? newInterval
                    : _registry.ExpectedIntervalFor(candidate));

            PlacementResultDto placement = validator.CheckPlacement(_topology.Snapshot(), nodeId);
            if (!placement.IsSafe)
            {
                _topology.RemoveNode(nodeId);
                string[] messages = placement.Issues
                    .Where(i => i.Severity == DeploymentTreeValidator.Error)
                    .Select(i => $"{i.Path}: {i.Message}")
                    .ToArray();

                return GatewayOutcome.Invalid<SensorDevice>(new Dictionary<string, string[]>
                {
                    ["placement"] = messages.Length > 0 ? messages : new[] { "The device cannot be safely placed in this Sub-Zone." }
                });
            }

            var device = new SensorDevice
            {
                MacAddress = mac,
                DisplayName = request.DisplayName.Trim(),
                Category = request.Category,
                ValueKind = request.Category.DefaultKind(),
                Metric = request.Metric.Trim(),
                Unit = request.Category == SensorCategory.Actuator ? string.Empty : (request.Unit ?? string.Empty).Trim(),
                ExpectedIntervalSeconds = request.ExpectedIntervalSeconds,
                MinValue = request.Category == SensorCategory.Actuator ? null : request.MinValue,
                MaxValue = request.Category == SensorCategory.Actuator ? null : request.MaxValue,
                Location = location,
                RegisteredAt = _time.GetUtcNow(),
                IsSimulated = request.SimulateTelemetry
            };

            if (!_registry.TryAdd(device))
            {
                _topology.RemoveNode(nodeId);
                return GatewayOutcome.Invalid<SensorDevice>(nameof(request.MacAddress), $"{mac} is already registered.");
            }

            _pulse.Track(device);
            _commands.Track(device); // actuators get a device shadow for commands
            return GatewayOutcome.Ok(device);
        }
    }

    private static void AddError(Dictionary<string, string[]> errors, string key, string message)
    {
        string camel = string.IsNullOrEmpty(key) ? "request" : char.ToLowerInvariant(key[0]) + key[1..];
        errors[camel] = errors.TryGetValue(camel, out string[]? existing)
            ? existing.Append(message).ToArray()
            : new[] { message };
    }
}
