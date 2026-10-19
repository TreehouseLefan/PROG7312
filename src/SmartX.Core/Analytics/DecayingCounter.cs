namespace SmartX.Core.Analytics;

/// <summary>
/// A count that halves every <c>halfLife</c>, so recent operator behaviour outweighs old habits
/// (concept drift). Stored as the value at the last update and decayed lazily when read.
/// </summary>
public struct DecayingCounter
{
    public double Value { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public readonly double ValueAt(DateTimeOffset now, TimeSpan halfLife)
    {
        if (Value == 0 || halfLife <= TimeSpan.Zero)
        {
            return Value;
        }

        double elapsed = (now - UpdatedAt).TotalSeconds;
        if (elapsed <= 0)
        {
            return Value;
        }

        return Value * Math.Pow(0.5, elapsed / halfLife.TotalSeconds);
    }

    public void Add(double amount, DateTimeOffset at, TimeSpan halfLife)
    {
        if (Value == 0 && UpdatedAt == default)
        {
            Value = amount;
            UpdatedAt = at;
            return;
        }

        if (at >= UpdatedAt)
        {
            Value = ValueAt(at, halfLife) + amount;
            UpdatedAt = at;
        }
        else
        {
            // An older event (e.g. seeded history) is decayed forward to the current reference time.
            Value += amount * Math.Pow(0.5, (UpdatedAt - at).TotalSeconds / halfLife.TotalSeconds);
        }
    }
}
