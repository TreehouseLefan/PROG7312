using System.Diagnostics;
using System.Text.Json;
using SmartX.Core.Contracts;
using SmartX.Core.Devices;
using SmartX.Core.Pulse;
using SmartX.Core.Streaming;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// The validation gateway. Raw JSON is checked (known device, declared payload type, timestamp,
/// finite value, plausible range) and turned into a strongly typed <see cref="TelemetryPacket{T}"/>
/// so no value is ever boxed.
/// <para>
/// Part 2: accepted packets are no longer processed on the request thread. They are classified and
/// enqueued: critical readings (a severe power spike, a critical moisture drop) go to the priority lane,
/// everything else to the FIFO lane. A single background dispatcher drains the queue and calls
/// <see cref="Process"/>, which stores the reading, updates the Pulse engine, the sorted timeline, the
/// alert sets and the device shadows.
/// </para>
/// </summary>
public sealed class TelemetryIngestionService
{
    private const int MaxBatches = 100;
    private const int MaxReadingsPerBatch = 2_000;
    private const int MaxReadingsPerUpload = 50_000;

    private readonly DeviceRegistry _registry;
    private readonly TelemetryStore _store;
    private readonly PulseMonitor _pulse;
    private readonly IngestPipeline _pipeline;
    private readonly AlertCenter _alerts;
    private readonly TimelineService _timeline;
    private readonly CommandService _commands;
    private readonly TimeProvider _time;
    private readonly object _rejectionGate = new();
    private readonly Dictionary<MacKey, Queue<DateTimeOffset>> _rejections = new();
    private readonly DateTimeOffset _startedAt;
    private readonly object _rateGate = new();

    private long _accepted;
    private long _rejected;
    private long _rateLastCount;
    private DateTimeOffset _rateLastTime;
    private double _rate;

    public TelemetryIngestionService(
        DeviceRegistry registry,
        TelemetryStore store,
        PulseMonitor pulse,
        IngestPipeline pipeline,
        AlertCenter alerts,
        TimelineService timeline,
        CommandService commands,
        TimeProvider time)
    {
        _registry = registry;
        _store = store;
        _pulse = pulse;
        _pipeline = pipeline;
        _alerts = alerts;
        _timeline = timeline;
        _commands = commands;
        _time = time;
        _startedAt = time.GetUtcNow();
        _rateLastTime = _startedAt;
    }

    private delegate bool ElementParser<T>(JsonElement element, out T value);

    // ------------------------------------------------------------------
    // Single reading from JSON
    // ------------------------------------------------------------------

    public GatewayOutcome<TelemetryIngestResult> Ingest(TelemetryIngestRequest request)
    {
        if (request is null)
        {
            return Reject<TelemetryIngestResult>("request", "A request body is required.");
        }

        if (!TelemetryValueKindExtensions.TryParseWireName(request.PayloadType, out TelemetryValueKind kind))
        {
            return Reject<TelemetryIngestResult>("payloadType", "payloadType must be 'float', 'int' or 'bool'.");
        }

        switch (kind)
        {
            case TelemetryValueKind.Float:
            {
                if (!TryParseFloat(request.Value, out float value))
                {
                    return Reject<TelemetryIngestResult>("value", "Expected a finite number for a float payload.");
                }

                return IngestValue(request.DeviceMac, value, request.Timestamp);
            }

            case TelemetryValueKind.Int:
            {
                if (!TryParseInt(request.Value, out int value))
                {
                    return Reject<TelemetryIngestResult>("value", "Expected a whole number for an int payload.");
                }

                return IngestValue(request.DeviceMac, value, request.Timestamp);
            }

            default:
            {
                if (!TryParseBool(request.Value, out bool value))
                {
                    return Reject<TelemetryIngestResult>("value", "Expected true or false for a bool payload.");
                }

                return IngestValue(request.DeviceMac, value, request.Timestamp);
            }
        }
    }

    // ------------------------------------------------------------------
    // Single strongly typed reading: validate, classify, enqueue
    // ------------------------------------------------------------------

    public GatewayOutcome<TelemetryIngestResult> IngestValue<T>(string? deviceMac, T value, DateTimeOffset? timestamp)
        where T : struct, IEquatable<T>
    {
        TelemetryValueKind kind = TelemetryValueConverter.KindOf<T>();
        GatewayOutcome<TelemetryIngestResult>? failure = ValidateCommon<TelemetryIngestResult>(
            deviceMac,
            kind,
            timestamp,
            out SensorDevice? device,
            out DateTimeOffset resolvedTimestamp);

        if (failure is not null || device is null)
        {
            return failure ?? Reject<TelemetryIngestResult>("deviceMac", "Unknown device.");
        }

        var packet = new TelemetryPacket<T>(device.MacAddress, device.Category, device.Metric, value, resolvedTimestamp, device.Unit);
        double numeric = packet.NumericValue;

        if (!IsInRange(device, numeric))
        {
            RecordRejection(device, numeric, packet.Timestamp);
            return Reject<TelemetryIngestResult>(
                "value",
                $"{numeric} {device.Unit} is outside the plausible range {device.MinValue}..{device.MaxValue} for {device.Metric}.");
        }

        // Classify at the door: critical readings jump the queue.
        (AlertSeverity Severity, string Reason)? critical = _alerts.ClassifyCritical(device, numeric);
        var envelope = new IngestEnvelope
        {
            Device = device,
            Packet = packet,
            PriorityReason = critical?.Reason
        };

        if (!_pipeline.TryEnqueue(envelope, critical?.Severity, out IngestLane lane, out long sequence))
        {
            Interlocked.Increment(ref _rejected);
            return GatewayOutcome.Overloaded<TelemetryIngestResult>(
                "The standard ingestion queue is full. Retry shortly; critical readings are still accepted.");
        }

        Interlocked.Increment(ref _accepted);
        return GatewayOutcome.Ok(new TelemetryIngestResult(
            true,
            device.MacAddress,
            device.Metric,
            numeric,
            packet.Timestamp,
            lane,
            critical?.Reason,
            sequence,
            _pipeline.Queue.StandardCount,
            _pipeline.Queue.PriorityCount));
    }

    // ------------------------------------------------------------------
    // Queue consumer (called by TelemetryDispatcher, one packet at a time)
    // ------------------------------------------------------------------

    public void Process(Dequeued<IngestEnvelope> item)
    {
        IngestEnvelope envelope = item.Item;
        if (envelope.Synthetic)
        {
            _pipeline.RecordProcessed(item, null); // load-test packet: timed, then discarded
            return;
        }

        SensorDevice device = envelope.Device;
        ITelemetryPacket packet = envelope.Packet;
        double value = packet.NumericValue;
        DateTimeOffset timestamp = packet.Timestamp;

        _store.Append(packet);
        _pulse.OnReading(device, value, timestamp);
        _timeline.Record(device.Key, timestamp, value);

        if (device.IsActuator)
        {
            _commands.OnReported(device.Key, value >= 0.5, timestamp);
        }

        _alerts.EvaluateReading(device, value, timestamp);

        _pipeline.RecordProcessed(item, new StreamPacketDto(
            item.Sequence,
            device.MacAddress,
            device.DisplayName,
            device.Metric,
            device.Unit,
            device.ValueKind.ToWireName(),
            Math.Round(value, 2),
            timestamp,
            item.Lane,
            item.Severity,
            envelope.PriorityReason,
            Math.Round(item.WaitMilliseconds, 3),
            device.Location.SubZoneId));
    }

    /// <summary>Out-of-range readings rejected for this device since <paramref name="since"/>.</summary>
    public int RecentRejections(MacKey mac, DateTimeOffset since)
    {
        lock (_rejectionGate)
        {
            if (!_rejections.TryGetValue(mac, out Queue<DateTimeOffset>? times))
            {
                return 0;
            }

            int count = 0;
            foreach (DateTimeOffset time in times)
            {
                if (time >= since)
                {
                    count++;
                }
            }

            return count;
        }
    }

    private void RecordRejection(SensorDevice device, double value, DateTimeOffset at)
    {
        lock (_rejectionGate)
        {
            if (!_rejections.TryGetValue(device.Key, out Queue<DateTimeOffset>? times))
            {
                times = new Queue<DateTimeOffset>();
                _rejections[device.Key] = times;
            }

            times.Enqueue(at);
            while (times.Count > 50)
            {
                times.Dequeue();
            }
        }

        _alerts.Raise(
            device,
            AlertType.OutOfRange,
            AlertSeverity.Elevated,
            $"{device.DisplayName} sent {value:0.##} {device.Unit}, outside its plausible range {device.MinValue}..{device.MaxValue}",
            at);
    }

    // ------------------------------------------------------------------
    // Historical batch upload (jagged arrays)
    // ------------------------------------------------------------------

    public GatewayOutcome<TelemetryBatchResult> IngestBatch(TelemetryBatchRequest request)
    {
        if (request is null)
        {
            return Reject<TelemetryBatchResult>("request", "A request body is required.");
        }

        if (!TelemetryValueKindExtensions.TryParseWireName(request.PayloadType, out TelemetryValueKind kind))
        {
            return Reject<TelemetryBatchResult>("payloadType", "payloadType must be 'float', 'int' or 'bool'.");
        }

        if (request.Batches is null || request.Batches.Length == 0)
        {
            return Reject<TelemetryBatchResult>("batches", "Provide at least one batch of readings.");
        }

        if (request.Batches.Length > MaxBatches)
        {
            return Reject<TelemetryBatchResult>("batches", $"A single upload may contain at most {MaxBatches} batches.");
        }

        int total = 0;
        foreach (JsonElement[]? batch in request.Batches)
        {
            int length = batch?.Length ?? 0;
            if (length > MaxReadingsPerBatch)
            {
                return Reject<TelemetryBatchResult>("batches", $"Each batch may contain at most {MaxReadingsPerBatch} readings.");
            }

            total += length;
        }

        if (total == 0 || total > MaxReadingsPerUpload)
        {
            return Reject<TelemetryBatchResult>("batches", $"An upload must contain between 1 and {MaxReadingsPerUpload} readings.");
        }

        if (request.IntervalSeconds < 1 || request.IntervalSeconds > 3600)
        {
            return Reject<TelemetryBatchResult>("intervalSeconds", "intervalSeconds must be between 1 and 3600.");
        }

        TimeSpan interval = TimeSpan.FromSeconds(request.IntervalSeconds);
        DateTimeOffset first = request.FirstTimestamp ?? _time.GetUtcNow() - (interval * total);

        GatewayOutcome<TelemetryBatchResult>? failure = ValidateCommon<TelemetryBatchResult>(
            request.DeviceMac,
            kind,
            first,
            out SensorDevice? device,
            out DateTimeOffset resolvedFirst);

        if (failure is not null || device is null)
        {
            return failure ?? Reject<TelemetryBatchResult>("deviceMac", "Unknown device.");
        }

        string? error;
        switch (kind)
        {
            case TelemetryValueKind.Float:
                if (TryConvertBatches<float>(request.Batches, TryParseFloat, out float[][] floats, out error))
                {
                    return GatewayOutcome.Ok(IngestTyped(device, floats, resolvedFirst, interval));
                }

                break;

            case TelemetryValueKind.Int:
                if (TryConvertBatches<int>(request.Batches, TryParseInt, out int[][] ints, out error))
                {
                    return GatewayOutcome.Ok(IngestTyped(device, ints, resolvedFirst, interval));
                }

                break;

            default:
                if (TryConvertBatches<bool>(request.Batches, TryParseBool, out bool[][] bools, out error))
                {
                    return GatewayOutcome.Ok(IngestTyped(device, bools, resolvedFirst, interval));
                }

                break;
        }

        return Reject<TelemetryBatchResult>("batches", error ?? "The batch could not be parsed.");
    }

    /// <summary>Used by the seeder to backfill history with typed jagged arrays.</summary>
    public TelemetryBatchResult IngestHistorical<T>(SensorDevice device, T[][] batches, DateTimeOffset first, TimeSpan interval)
        where T : struct, IEquatable<T> =>
        IngestTyped(device, batches, first, interval);

    public IngestStatsDto GetStats()
    {
        DateTimeOffset now = _time.GetUtcNow();
        long accepted = Interlocked.Read(ref _accepted);
        double rate;

        lock (_rateGate)
        {
            double elapsed = (now - _rateLastTime).TotalSeconds;
            if (elapsed >= 1)
            {
                _rate = (accepted - _rateLastCount) / elapsed;
                _rateLastCount = accepted;
                _rateLastTime = now;
            }

            rate = _rate;
        }

        return new IngestStatsDto(accepted, Interlocked.Read(ref _rejected), Math.Round(rate, 1), _registry.Count, _startedAt);
    }

    private TelemetryBatchResult IngestTyped<T>(SensorDevice device, T[][] batches, DateTimeOffset first, TimeSpan interval)
        where T : struct, IEquatable<T>
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();

        // Jagged array → pre-sized List<TelemetryPacket<T>>
        List<TelemetryPacket<T>> packets = TelemetryBatchProcessor.FlattenBatches(
            batches,
            device.MacAddress,
            device.Category,
            device.Metric,
            device.Unit,
            first,
            interval);

        var accepted = new List<TelemetryPacket<T>>(packets.Count);
        int rejected = 0;
        foreach (TelemetryPacket<T> packet in packets)
        {
            if (IsInRange(device, packet.NumericValue))
            {
                accepted.Add(packet);
            }
            else
            {
                rejected++;
            }
        }

        _store.GetOrCreate<T>(device.MacAddress).AddRange(accepted);

        for (int i = 0; i < accepted.Count; i++)
        {
            // Only the newest reading is evaluated against "now"; older ones just build the baseline.
            _pulse.OnReading(device, accepted[i].NumericValue, accepted[i].Timestamp, evaluate: i == accepted.Count - 1);
            _timeline.Record(device.Key, accepted[i].Timestamp, accepted[i].NumericValue);
        }

        stopwatch.Stop();
        Interlocked.Add(ref _accepted, accepted.Count);
        Interlocked.Add(ref _rejected, rejected);

        return new TelemetryBatchResult(
            device.MacAddress,
            batches.Length,
            accepted.Count,
            rejected,
            packets.Capacity,
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            TelemetryBatchProcessor.Summarise(batches));
    }

    private GatewayOutcome<TResult>? ValidateCommon<TResult>(
        string? deviceMac,
        TelemetryValueKind kind,
        DateTimeOffset? timestamp,
        out SensorDevice? device,
        out DateTimeOffset resolvedTimestamp) where TResult : class
    {
        device = null;
        DateTimeOffset now = _time.GetUtcNow();
        resolvedTimestamp = timestamp ?? now;

        if (!MacAddress.TryNormalize(deviceMac, out string mac))
        {
            return Reject<TResult>("deviceMac", "deviceMac must be a valid MAC address, e.g. 24:0A:C4:12:34:56.");
        }

        if (!_registry.TryGet(mac, out SensorDevice? found))
        {
            Interlocked.Increment(ref _rejected);
            return GatewayOutcome.NotFound<TResult>($"Device {mac} is not registered. Register it before sending telemetry.");
        }

        if (found.ValueKind != kind)
        {
            return Reject<TResult>(
                "payloadType",
                $"Device {mac} is registered for '{found.ValueKind.ToWireName()}' payloads, not '{kind.ToWireName()}'.");
        }

        if (resolvedTimestamp > now.AddMinutes(5))
        {
            return Reject<TResult>("timestamp", "The timestamp is more than 5 minutes in the future (check the device clock).");
        }

        if (resolvedTimestamp < now.AddHours(-24))
        {
            return Reject<TResult>("timestamp", "The timestamp is older than 24 hours.");
        }

        device = found;
        return null;
    }

    private GatewayOutcome<TResult> Reject<TResult>(string field, string message) where TResult : class
    {
        Interlocked.Increment(ref _rejected);
        return GatewayOutcome.Invalid<TResult>(field, message);
    }

    private static bool IsInRange(SensorDevice device, double value)
    {
        if (device.MinValue is double min && value < min)
        {
            return false;
        }

        if (device.MaxValue is double max && value > max)
        {
            return false;
        }

        return true;
    }

    private static bool TryConvertBatches<T>(
        JsonElement[][] raw,
        ElementParser<T> parser,
        out T[][] batches,
        out string? error)
    {
        batches = new T[raw.Length][];

        for (int b = 0; b < raw.Length; b++)
        {
            JsonElement[] row = raw[b] ?? Array.Empty<JsonElement>();
            var typed = new T[row.Length];

            for (int i = 0; i < row.Length; i++)
            {
                if (!parser(row[i], out typed[i]))
                {
                    error = $"Batch {b}, reading {i} is not a valid value for this payload type.";
                    return false;
                }
            }

            batches[b] = typed;
        }

        error = null;
        return true;
    }

    private static bool TryParseFloat(JsonElement element, out float value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number &&
               element.TryGetSingle(out value) &&
               float.IsFinite(value);
    }

    private static bool TryParseInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    private static bool TryParseBool(JsonElement element, out bool value)
    {
        value = element.ValueKind == JsonValueKind.True;
        return element.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }
}
