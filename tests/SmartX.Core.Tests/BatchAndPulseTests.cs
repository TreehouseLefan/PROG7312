using SmartX.Core.Pulse;
using SmartX.Core.Telemetry;

namespace SmartX.Core.Tests;

public class BatchAndPulseTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Jagged_batches_flatten_into_a_presized_list()
    {
        float[][] batches =
        {
            new[] { 1f, 2f, 3f },
            new[] { 4f },
            null!,
            new[] { 5f, 6f }
        };

        List<TelemetryPacket<float>> packets = TelemetryBatchProcessor.FlattenBatches(
            batches, "24:0A:C4:00:00:01", SensorCategory.Environmental, "soil_moisture", "%", Start, TimeSpan.FromSeconds(2));

        Assert.Equal(6, packets.Count);
        Assert.Equal(6, packets.Capacity);
        Assert.Equal(Start.AddSeconds(10), packets[^1].Timestamp);
        Assert.Equal(6f, packets[^1].Value);
    }

    [Fact]
    public void History_matrix_is_rectangular_and_serialises_nan_as_null()
    {
        var series = new List<double[]> { new double[] { 1, 1, 3, 3 }, Array.Empty<double>() };

        double[,] matrix = TelemetryBatchProcessor.BuildHistoryMatrix(series, 2);
        double?[][] jagged = TelemetryBatchProcessor.ToJagged(matrix);

        Assert.Equal(2, matrix.GetLength(0));
        Assert.Equal(2, matrix.GetLength(1));
        Assert.Equal(1d, jagged[0][0]);
        Assert.Equal(3d, jagged[0][1]);
        Assert.Null(jagged[1][0]);
    }

    [Fact]
    public void Spike_needs_three_consecutive_breaches()
    {
        var tracker = new DeviceHealthTracker(TelemetryValueKind.Float, 2, new PulseOptions());
        DateTimeOffset t = Start;

        for (int i = 0; i < 30; i++)
        {
            tracker.RecordReading(i % 2 == 0 ? 49.5 : 50.5, t);
            t = t.AddSeconds(2);
        }

        Assert.Equal(PulseState.Healthy, tracker.RecordReading(90, t));
        Assert.Equal(PulseState.Healthy, tracker.RecordReading(90, t = t.AddSeconds(2)));
        Assert.Equal(PulseState.Spike, tracker.RecordReading(90, t = t.AddSeconds(2)));
        Assert.Equal(PulseState.Spike, tracker.Evaluate(t.AddSeconds(1)));
    }

    [Fact]
    public void Silence_fades_then_goes_offline()
    {
        var tracker = new DeviceHealthTracker(TelemetryValueKind.Float, 2, new PulseOptions());
        DateTimeOffset t = Start;

        for (int i = 0; i < 30; i++)
        {
            tracker.RecordReading(50, t);
            t = t.AddSeconds(2);
        }

        DateTimeOffset last = t.AddSeconds(-2);
        Assert.Equal(PulseState.Healthy, tracker.Evaluate(last.AddSeconds(1)));
        Assert.Equal(PulseState.Fading, tracker.Evaluate(last.AddSeconds(6)));
        Assert.Equal(PulseState.Offline, tracker.Evaluate(last.AddSeconds(30)));
        Assert.Equal(PulseState.Healthy, tracker.RecordReading(50, last.AddSeconds(31)));
    }

    [Fact]
    public void Chattering_actuator_is_flagged()
    {
        var tracker = new DeviceHealthTracker(TelemetryValueKind.Bool, 5, new PulseOptions());
        DateTimeOffset t = Start;
        PulseState state = PulseState.Unknown;

        for (int i = 0; i < 10; i++)
        {
            state = tracker.RecordReading(i % 2, t);
            t = t.AddSeconds(5);
        }

        Assert.Equal(PulseState.Spike, state);
    }

    [Fact]
    public void Phi_grows_with_silence()
    {
        double early = PhiAccrual.Calculate(1, 4, 1);
        double late = PhiAccrual.Calculate(6, 4, 1);
        double dead = PhiAccrual.Calculate(60, 4, 1);

        Assert.True(early < 0.1);
        Assert.InRange(late, 1, 8);
        Assert.Equal(PhiAccrual.MaxPhi, dead);
    }
}
