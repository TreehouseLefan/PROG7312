using SmartX.Core.Telemetry;

namespace SmartX.Core.Tests;

public class TelemetryPacketTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Float_packet_exposes_typed_and_numeric_value()
    {
        var packet = new TelemetryPacket<float>("24:0A:C4:00:00:01", SensorCategory.Environmental, "soil_moisture", 42.5f, Now, "%");

        Assert.Equal(42.5f, packet.Value);
        Assert.Equal(42.5, packet.NumericValue, 3);
        Assert.Equal(TelemetryValueKind.Float, packet.ValueKind);
    }

    [Fact]
    public void Int_and_bool_packets_convert_without_boxing()
    {
        var watts = new TelemetryPacket<int>("24:0A:C4:00:00:02", SensorCategory.PowerConsumption, "power_draw", 1250, Now, "W");
        var valve = new TelemetryPacket<bool>("24:0A:C4:00:00:03", SensorCategory.Actuator, "valve_state", true, Now);

        Assert.Equal(1250d, watts.NumericValue);
        Assert.Equal(TelemetryValueKind.Int, watts.ValueKind);
        Assert.Equal(1d, valve.NumericValue);
        Assert.Equal(TelemetryValueKind.Bool, valve.ValueKind);
    }

    [Fact]
    public void Heterogeneous_packets_share_the_non_generic_interface()
    {
        var packets = new List<ITelemetryPacket>
        {
            new TelemetryPacket<float>("24:0A:C4:00:00:01", SensorCategory.Environmental, "humidity", 60f, Now),
            new TelemetryPacket<int>("24:0A:C4:00:00:02", SensorCategory.PowerConsumption, "power_draw", 900, Now),
            new TelemetryPacket<bool>("24:0A:C4:00:00:03", SensorCategory.Actuator, "switch_state", false, Now)
        };

        Assert.Equal(960d, packets.Sum(p => p.NumericValue), 3);
    }

    [Fact]
    public void Same_value_comparison_uses_generic_equality()
    {
        var a = new TelemetryPacket<int>("24:0A:C4:00:00:02", SensorCategory.PowerConsumption, "power_draw", 700, Now);
        TelemetryPacket<int> b = a.WithValue(700, Now.AddSeconds(3));
        TelemetryPacket<int> c = a.WithValue(701, Now.AddSeconds(6));

        Assert.True(a.HasSameValueAs(b));
        Assert.False(a.HasSameValueAs(c));
    }

    [Fact]
    public void Unsupported_payload_type_is_rejected()
    {
        Assert.Throws<NotSupportedException>(() =>
            new TelemetryPacket<decimal>("24:0A:C4:00:00:09", SensorCategory.Environmental, "x", 1m, Now));
    }

    [Fact]
    public void Ring_buffer_overwrites_the_oldest_items()
    {
        var buffer = new RingBuffer<int>(3);
        for (int i = 1; i <= 5; i++)
        {
            buffer.Add(i);
        }

        Assert.Equal(new[] { 3, 4, 5 }, buffer.ToArray());
        Assert.Equal(5, buffer.Newest);
        Assert.Equal(new[] { 4, 5 }, buffer.ToArray(2));
    }
}
