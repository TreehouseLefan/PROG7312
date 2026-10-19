namespace SmartX.Core.Analytics;

/// <summary>Result of an ordinary least-squares fit y = Intercept + Slope · x.</summary>
public readonly record struct TrendFit(double Slope, double Intercept, double RSquared, int Count)
{
    public double Predict(double x) => Intercept + (Slope * x);

    /// <summary>
    /// Seconds from <paramref name="x"/> until the fitted line reaches <paramref name="threshold"/>,
    /// or null if the line is moving away from it (or is flat).
    /// </summary>
    public double? SecondsUntil(double threshold, double x)
    {
        if (Math.Abs(Slope) < 1e-9)
        {
            return null;
        }

        double crossing = (threshold - Intercept) / Slope;
        double seconds = crossing - x;
        return seconds >= 0 && double.IsFinite(seconds) ? seconds : null;
    }
}

public static class LinearTrend
{
    /// <summary>Ordinary least squares over paired samples. Returns null with fewer than three points.</summary>
    public static TrendFit? Fit(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        int n = Math.Min(x.Length, y.Length);
        if (n < 3)
        {
            return null;
        }

        double meanX = 0, meanY = 0;
        for (int i = 0; i < n; i++)
        {
            meanX += x[i];
            meanY += y[i];
        }

        meanX /= n;
        meanY /= n;

        double sxx = 0, sxy = 0, syy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - meanX;
            double dy = y[i] - meanY;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }

        if (sxx < 1e-12)
        {
            return null;
        }

        double slope = sxy / sxx;
        double intercept = meanY - (slope * meanX);
        double r2 = syy < 1e-12 ? 1 : (sxy * sxy) / (sxx * syy);
        return new TrendFit(slope, intercept, r2, n);
    }
}
