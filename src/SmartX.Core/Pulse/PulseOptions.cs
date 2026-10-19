namespace SmartX.Core.Pulse;

/// <summary>Tuning values for the Sensor Pulse Grid. Bound from the "Pulse" section of appsettings.json.</summary>
public sealed class PulseOptions
{
    /// <summary>How many recent readings form a device's rolling baseline.</summary>
    public int BaselineWindow { get; set; } = 60;

    /// <summary>Readings required before spike detection starts.</summary>
    public int MinimumSamples { get; set; } = 12;

    /// <summary>|z| above this value counts as a breach (mean ± 3σ band).</summary>
    public double ZScoreLimit { get; set; } = 3.0;

    /// <summary>The k in "k consecutive breaches" before a tile turns into a Spike.</summary>
    public int ConsecutiveBreachesForSpike { get; set; } = 3;

    /// <summary>After this many consecutive breaches the baseline starts accepting the new level (drift).</summary>
    public int AdaptAfterBreaches { get; set; } = 20;

    /// <summary>Minimum time a Spike stays visible so an operator can notice it.</summary>
    public double SpikeHoldSeconds { get; set; } = 10;

    /// <summary>How many heartbeat inter-arrival times the φ detector remembers.</summary>
    public int HeartbeatWindow { get; set; } = 40;

    /// <summary>Lower bound for the heartbeat standard deviation, as a fraction of the expected interval.</summary>
    public double MinStdFractionOfInterval { get; set; } = 0.5;

    /// <summary>Allowed pause before suspicion rises, as a multiple of the expected interval.</summary>
    public double AcceptablePauseFactor { get; set; } = 1.0;

    /// <summary>φ at which a tile starts to fade.</summary>
    public double FadingPhi { get; set; } = 1.0;

    /// <summary>φ at which a tile is declared offline.</summary>
    public double OfflinePhi { get; set; } = 8.0;

    /// <summary>Window (in readings) used to detect a chattering actuator.</summary>
    public int ChatterWindow { get; set; } = 10;

    /// <summary>Toggles within the window that count as chatter.</summary>
    public int ChatterToggleLimit { get; set; } = 6;
}
