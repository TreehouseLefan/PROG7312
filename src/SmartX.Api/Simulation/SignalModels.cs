using System.Globalization;

namespace SmartX.Api.Simulation;

    /// <summary>Shape of a simulated sensor signal: slow sine wave and Gaussian noise and optional spike.</summary>
public sealed record SignalModel(double Base, double Amplitude, double Noise, double PeriodSeconds, double SpikeOffset);

public static class SignalModels
{
    public static SignalModel For(string metric) => metric switch
    {
        "soil_moisture" => new SignalModel(42, 4, 0.6, 600, 35),
        "air_temperature" => new SignalModel(24, 3, 0.3, 900, 18),
        "humidity" => new SignalModel(65, 6, 0.8, 700, -30),
        "power_draw" => new SignalModel(1200, 250, 40, 480, 4200),
        _ => new SignalModel(50, 5, 1, 600, 30)
    };

    public static double Sample(SignalModel model, string mac, DateTimeOffset at, bool spike)
    {
        int lastByte = LastByte(mac);
        double phase = lastByte / 255d * Math.PI * 2;
        double offset = ((lastByte % 7) - 3) * model.Amplitude * 0.3;
        double seconds = at.ToUnixTimeMilliseconds() / 1000d;

        double value = model.Base
                       + offset
                       + (model.Amplitude * Math.Sin((2 * Math.PI * seconds / model.PeriodSeconds) + phase))
                       + (Gaussian() * model.Noise);

        if (spike)
        {
            value += model.SpikeOffset * (0.9 + (Random.Shared.NextDouble() * 0.2));
        }

        return value;
    }

    /// <summary>BoxMuller transform for normally distributed noise.</summary>
    public static double Gaussian()
    {
        double u1 = 1.0 - Random.Shared.NextDouble();
        double u2 = Random.Shared.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static int LastByte(string mac)
    {
        if (mac is { Length: >= 2 } &&
            int.TryParse(mac[^2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        return 0;
    }
}
