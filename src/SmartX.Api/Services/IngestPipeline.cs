using SmartX.Core.Contracts;
using SmartX.Core.Streaming;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>A validated packet waiting to be processed.</summary>
public sealed class IngestEnvelope
{
    public required SensorDevice Device { get; init; }

    /// <summary>The typed packet behind the non-generic interface (no boxing: the packet is a class).</summary>
    public required ITelemetryPacket Packet { get; init; }

    /// <summary>Why the packet took the priority lane; null for standard packets.</summary>
    public string? PriorityReason { get; init; }

    /// <summary>Load-test packets travel the real queues but are discarded after timing.</summary>
    public bool Synthetic { get; init; }

    /// <summary>Marks the single critical packet in a burst test.</summary>
    public bool IsProbe { get; init; }
}

/// <summary>
/// Owns the two-lane ingestion queue (<see cref="DualLaneQueue{T}"/>), the pause switch used in demos, the
/// recent-packet feed for the Command Stream page, and wait-time statistics per lane.
/// </summary>
public sealed class IngestPipeline
{
    private const int FeedCapacity = 150;
    private const int WaitSamples = 512;

    private readonly object _gate = new();
    private readonly RingBuffer<StreamPacketDto> _recent = new(FeedCapacity);
    private readonly Queue<StreamPacketDto> _pendingPush = new();
    private readonly RingBuffer<double> _standardWaits = new(WaitSamples);
    private readonly RingBuffer<double> _priorityWaits = new(WaitSamples);
    private readonly TimeProvider _time;
    private long _standardProcessed;
    private long _priorityProcessed;
    private long _rateCount;
    private DateTimeOffset _rateAt;
    private double _rate;
    private volatile bool _paused;
    private BurstTracker? _burst;

    public IngestPipeline(TimeProvider time)
    {
        _time = time;
        _rateAt = time.GetUtcNow();
    }

    public DualLaneQueue<IngestEnvelope> Queue { get; } = new(standardCapacity: 50_000);

    public bool Paused => _paused;

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    /// <summary>Routes a packet: critical packets to the priority heap, everything else to the FIFO queue.</summary>
    public bool TryEnqueue(IngestEnvelope envelope, AlertSeverity? severity, out IngestLane lane, out long sequence)
    {
        if (severity is AlertSeverity level)
        {
            lane = IngestLane.Priority;
            sequence = Queue.EnqueuePriority(envelope, level);
            return true;
        }

        lane = IngestLane.Standard;
        return Queue.TryEnqueueStandard(envelope, out sequence);
    }

    /// <summary>Called by the dispatcher after each packet. <paramref name="packet"/> is null for synthetic packets.</summary>
    public void RecordProcessed(Dequeued<IngestEnvelope> item, StreamPacketDto? packet)
    {
        lock (_gate)
        {
            if (item.Lane == IngestLane.Priority)
            {
                _priorityProcessed++;
                _priorityWaits.Add(item.WaitMilliseconds);
            }
            else
            {
                _standardProcessed++;
                _standardWaits.Add(item.WaitMilliseconds);
            }

            if (packet is not null)
            {
                _recent.Add(packet);
                _pendingPush.Enqueue(packet);
                while (_pendingPush.Count > FeedCapacity)
                {
                    _pendingPush.Dequeue();
                }
            }

            _burst?.Observe(item);
        }
    }

    /// <summary>Packets processed since the last broadcast, oldest first.</summary>
    public StreamPacketDto[] DrainPendingPush(int max)
    {
        lock (_gate)
        {
            var list = new List<StreamPacketDto>(Math.Min(max, _pendingPush.Count));
            while (list.Count < max && _pendingPush.TryDequeue(out StreamPacketDto? packet))
            {
                list.Add(packet);
            }

            return list.ToArray();
        }
    }

    /// <summary>Most recent processed packets, newest first.</summary>
    public StreamPacketDto[] Recent(int max)
    {
        lock (_gate)
        {
            StreamPacketDto[] oldestFirst = _recent.ToArray(max);
            Array.Reverse(oldestFirst);
            return oldestFirst;
        }
    }

    public QueueStatsDto Stats()
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            long processed = _standardProcessed + _priorityProcessed;
            double elapsed = (now - _rateAt).TotalSeconds;
            if (elapsed >= 1)
            {
                _rate = (processed - _rateCount) / elapsed;
                _rateCount = processed;
                _rateAt = now;
            }

            (double standardAvg, double standardP95) = Summarise(_standardWaits);
            (double priorityAvg, double priorityP95) = Summarise(_priorityWaits);

            return new QueueStatsDto(
                Queue.StandardCount,
                Queue.PriorityCount,
                Queue.StandardCapacity,
                _standardProcessed,
                _priorityProcessed,
                Queue.Shed,
                standardAvg,
                priorityAvg,
                standardP95,
                priorityP95,
                Math.Round(_rate, 1),
                _paused);
        }
    }

    /// <summary>
    /// Proves the bypass: pauses processing, enqueues <paramref name="count"/> standard packets and then one
    /// critical packet, resumes, and reports the position at which the critical packet was processed.
    /// </summary>
    public async Task<BurstReportDto> RunBurstAsync(
        int count,
        IReadOnlyList<SensorDevice> standardDevices,
        SensorDevice criticalDevice,
        Func<SensorDevice, ITelemetryPacket> normalPacket,
        ITelemetryPacket criticalPacket,
        CancellationToken cancellationToken)
    {
        count = Math.Clamp(count, 10, 40_000);
        var tracker = new BurstTracker(count + 1);
        bool wasPaused = _paused;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        lock (_gate)
        {
            if (_burst is not null)
            {
                throw new InvalidOperationException("A burst test is already running.");
            }

            _burst = tracker;
        }

        try
        {
            Pause();
            for (int i = 0; i < count; i++)
            {
                SensorDevice device = standardDevices[i % standardDevices.Count];
                var envelope = new IngestEnvelope { Device = device, Packet = normalPacket(device), Synthetic = true };
                if (!Queue.TryEnqueueStandard(envelope, out _))
                {
                    tracker.ExpectFewer();
                }
            }

            Queue.EnqueuePriority(
                new IngestEnvelope { Device = criticalDevice, Packet = criticalPacket, Synthetic = true, IsProbe = true },
                AlertSeverity.Critical);

            Resume();
            await tracker.Completed.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally
        {
            if (wasPaused)
            {
                Pause();
            }
            else
            {
                Resume();
            }

            lock (_gate)
            {
                _burst = null;
            }
        }

        stopwatch.Stop();
        string summary =
            $"{count:N0} standard packets were queued first, then one critical packet. The critical packet was processed " +
            $"#{tracker.ProbePosition} after waiting {tracker.ProbeWaitMs:0.###} ms; standard packets waited {tracker.StandardAverageMs:0.###} ms on average " +
            $"(max {tracker.StandardMaxMs:0.###} ms).";

        return new BurstReportDto(
            count,
            criticalDevice.DisplayName,
            tracker.ProbePosition,
            Math.Round(tracker.ProbeWaitMs, 4),
            Math.Round(tracker.StandardAverageMs, 4),
            Math.Round(tracker.StandardMaxMs, 4),
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
            summary);
    }

    private static (double Average, double P95) Summarise(RingBuffer<double> samples)
    {
        if (samples.Count == 0)
        {
            return (0, 0);
        }

        double[] values = samples.ToArray();
        Array.Sort(values);
        double sum = 0;
        foreach (double value in values)
        {
            sum += value;
        }

        int index = (int)Math.Ceiling(values.Length * 0.95) - 1;
        return (Math.Round(sum / values.Length, 3), Math.Round(values[Math.Clamp(index, 0, values.Length - 1)], 3));
    }

    private sealed class BurstTracker
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _expected;
        private int _seen;
        private int _standardSeen;
        private double _standardTotal;

        public BurstTracker(int expected)
        {
            _expected = expected;
        }

        public Task Completed => _completed.Task;
        public int ProbePosition { get; private set; }
        public double ProbeWaitMs { get; private set; }
        public double StandardMaxMs { get; private set; }
        public double StandardAverageMs => _standardSeen == 0 ? 0 : _standardTotal / _standardSeen;

        public void ExpectFewer() => _expected--;

        public void Observe(Dequeued<IngestEnvelope> item)
        {
            if (!item.Item.Synthetic)
            {
                return;
            }

            _seen++;
            if (item.Item.IsProbe)
            {
                ProbePosition = _seen;
                ProbeWaitMs = item.WaitMilliseconds;
            }
            else
            {
                _standardSeen++;
                _standardTotal += item.WaitMilliseconds;
                StandardMaxMs = Math.Max(StandardMaxMs, item.WaitMilliseconds);
            }

            if (_seen >= _expected)
            {
                _completed.TrySetResult();
            }
        }
    }
}
