using SmartX.Core.Readings;
using SmartX.Core.Telemetry;

namespace SmartX.Core.Pulse;

/// <summary>
/// Per-device state machine behind one Pulse Grid tile.
/// <list type="bullet">
/// <item>Spikes: rolling z-score with a k-consecutive-readings confirmation rule.</item>
/// <item>Chatter: an actuator toggling too often within a short window.</item>
/// <item>Silence: φ accrual suspicion computed from heartbeat inter-arrival times.</item>
/// </list>
/// Not thread-safe: callers must synchronise access.
/// </summary>
public sealed class DeviceHealthTracker
{
    private readonly PulseOptions _options;
    private readonly RingBuffer<double> _baseline;
    private readonly RingBuffer<double> _intervals;
    private readonly RingBuffer<bool> _toggles;
    private readonly double[] _scratch;

    private DateTimeOffset? _lastSeen;
    private bool? _lastBool;
    private int _consecutiveBreaches;
    private DateTimeOffset _spikeHoldUntil = DateTimeOffset.MinValue;
    private PulseState _readingState = PulseState.Unknown;

    public DeviceHealthTracker(TelemetryValueKind kind, int expectedIntervalSeconds, PulseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Kind = kind;
        ExpectedIntervalSeconds = Math.Max(1, expectedIntervalSeconds);
        _options = options;
        _baseline = new RingBuffer<double>(Math.Max(4, options.BaselineWindow));
        _intervals = new RingBuffer<double>(Math.Max(4, options.HeartbeatWindow));
        _toggles = new RingBuffer<bool>(Math.Max(2, options.ChatterWindow));
        _scratch = new double[_baseline.Capacity];
    }

    public TelemetryValueKind Kind { get; }

    public int ExpectedIntervalSeconds { get; }

    public PulseState State { get; private set; } = PulseState.Unknown;

    public double LastValue { get; private set; }

    public double LastZScore { get; private set; }

    public double Phi { get; private set; }

    public DateTimeOffset? LastSeen => _lastSeen;

    public int BaselineCount => _baseline.Count;

    public RunningStats BaselineStats()
    {
        int n = _baseline.CopyLatest(_scratch);
        return RunningStats.From(new ReadOnlySpan<double>(_scratch, 0, n));
    }

    /// <summary>Feeds one reading into the tracker and returns the reading-based state.</summary>
    public PulseState RecordReading(double value, DateTimeOffset timestamp)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Telemetry values must be finite.");
        }

        if (_lastSeen is DateTimeOffset previous)
        {
            double gap = (timestamp - previous).TotalSeconds;

            // Very long gaps are outages, not normal jitter, so they do not distort the heartbeat model.
            if (gap > 0 && gap <= ExpectedIntervalSeconds * 5d)
            {
                _intervals.Add(gap);
            }

            if (timestamp > previous)
            {
                _lastSeen = timestamp;
            }
        }
        else
        {
            _lastSeen = timestamp;
        }

        LastValue = value;
        Phi = 0;

        bool anomaly = Kind == TelemetryValueKind.Bool ? DetectChatter(value) : DetectSpike(value);

        if (anomaly)
        {
            double hold = Math.Max(_options.SpikeHoldSeconds, ExpectedIntervalSeconds * 2d);
            _spikeHoldUntil = timestamp.AddSeconds(hold);
            _readingState = PulseState.Spike;
        }
        else
        {
            _readingState = timestamp < _spikeHoldUntil ? PulseState.Spike : PulseState.Healthy;
        }

        State = _readingState;
        return State;
    }

    /// <summary>Combines the reading-based state with heartbeat suspicion at time <paramref name="now"/>.</summary>
    public PulseState Evaluate(DateTimeOffset now)
    {
        if (_lastSeen is not DateTimeOffset lastSeen)
        {
            Phi = 0;
            State = PulseState.Unknown;
            return State;
        }

        double elapsed = Math.Max(0, (now - lastSeen).TotalSeconds);
        (double mean, double std) = HeartbeatModel();
        Phi = Math.Round(PhiAccrual.Calculate(elapsed, mean, std), 2);

        if (_readingState == PulseState.Spike && now >= _spikeHoldUntil)
        {
            _readingState = PulseState.Healthy;
        }

        if (Phi >= _options.OfflinePhi)
        {
            State = PulseState.Offline;
        }
        else if (Phi >= _options.FadingPhi)
        {
            State = PulseState.Fading;
        }
        else
        {
            State = _readingState;
        }

        return State;
    }

    /// <summary>Mean and standard deviation of heartbeat intervals, with safety floors.</summary>
    public (double Mean, double Std) HeartbeatModel()
    {
        double expected = ExpectedIntervalSeconds;
        double mean;
        double std;

        if (_intervals.Count < 3)
        {
            mean = expected;
            std = expected * 0.25;
        }
        else
        {
            RunningStats stats = default;
            for (int i = 0; i < _intervals.Count; i++)
            {
                stats += _intervals[i];
            }

            mean = stats.Mean;
            std = stats.StandardDeviation;
        }

        std = Math.Max(std, expected * _options.MinStdFractionOfInterval);
        mean += expected * _options.AcceptablePauseFactor;
        return (mean, std);
    }

    private bool DetectSpike(double value)
    {
        if (_baseline.Count < _options.MinimumSamples)
        {
            _baseline.Add(value);
            LastZScore = 0;
            _consecutiveBreaches = 0;
            return false;
        }

        RunningStats stats = BaselineStats();

        // Floor the deviation so a perfectly flat signal does not turn tiny noise into huge z-scores.
        double sd = Math.Max(stats.StandardDeviation, (Math.Abs(stats.Mean) * 0.01) + 1e-6);
        double z = (value - stats.Mean) / sd;
        LastZScore = double.IsFinite(z) ? z : 0;

        bool breach = Math.Abs(LastZScore) > _options.ZScoreLimit;
        if (breach)
        {
            _consecutiveBreaches++;

            // Breaching values are kept out of the baseline so a spike cannot hide itself,
            // unless the new level persists long enough to be a genuine shift.
            if (_consecutiveBreaches >= _options.AdaptAfterBreaches)
            {
                _baseline.Add(value);
            }
        }
        else
        {
            _consecutiveBreaches = 0;
            _baseline.Add(value);
        }

        return _consecutiveBreaches >= _options.ConsecutiveBreachesForSpike;
    }

    private bool DetectChatter(double value)
    {
        bool current = value >= 0.5;
        bool toggled = _lastBool.HasValue && _lastBool.Value != current;
        _lastBool = current;
        _toggles.Add(toggled);

        int toggles = 0;
        for (int i = 0; i < _toggles.Count; i++)
        {
            if (_toggles[i])
            {
                toggles++;
            }
        }

        LastZScore = 0;
        return _toggles.IsFull && toggles >= _options.ChatterToggleLimit;
    }
}
