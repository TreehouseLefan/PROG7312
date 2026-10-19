namespace SmartX.Core.Pulse;

/// <summary>
/// φ accrual failure detector (Hayashibara et al., 2004). Instead of a single fixed timeout it
/// returns a continuous suspicion level: φ = -log10(P(a heartbeat arrives even later)).
/// The normal CDF is approximated with the logistic formula used by production implementations.
/// φ = 1 means roughly a 10% chance the device is still alive and simply late; φ = 8 means about 1 in 10^8.
/// </summary>
public static class PhiAccrual
{
    public const double MaxPhi = 99;

    public static double Calculate(double elapsedSeconds, double meanSeconds, double stdSeconds)
    {
        if (elapsedSeconds <= 0 || !double.IsFinite(elapsedSeconds))
        {
            return 0;
        }

        if (stdSeconds <= 0 || !double.IsFinite(stdSeconds))
        {
            stdSeconds = 1e-3;
        }

        double y = (elapsedSeconds - meanSeconds) / stdSeconds;
        double e = Math.Exp(-y * (1.5976 + (0.070566 * y * y)));

        double phi = elapsedSeconds > meanSeconds
            ? -Math.Log10(e / (1.0 + e))
            : -Math.Log10(1.0 - (1.0 / (1.0 + e)));

        if (double.IsNaN(phi))
        {
            return MaxPhi;
        }

        return Math.Clamp(phi, 0, MaxPhi);
    }
}
