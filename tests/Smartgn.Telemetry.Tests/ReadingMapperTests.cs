using Smartgn.Telemetry.Domain;
using Smartgn.Telemetry.Grpc;

namespace Smartgn.Telemetry.Tests;

public class ReadingMapperTests
{
    private static readonly DateTimeOffset MeasuredAt = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    private static TelemetryReading Reading(TelemetryFreshness freshness) =>
        new(
            "meter-001",
            "evt-0001",
            42,
            MeasuredAt,
            MeasuredAt.AddSeconds(2),
            new Dictionary<string, double> { ["voltage"] = 220.1, ["activeEnergy"] = 12.5 },
            freshness,
            7);

    [Fact]
    public void ToProto_MapsEveryContractField()
    {
        var proto = ReadingMapper.ToProto(Reading(TelemetryFreshness.Current));

        Assert.Equal("meter-001", proto.DeviceId);
        Assert.Equal("evt-0001", proto.EventId);
        Assert.Equal(42, proto.SequenceNumber);
        Assert.Equal(MeasuredAt.ToUnixTimeMilliseconds(), proto.MeasuredAtUnixMs);
        Assert.Equal(MeasuredAt.AddSeconds(2).ToUnixTimeMilliseconds(), proto.ReceivedAtUnixMs);
        Assert.Equal(7, proto.AssociationVersion);
        Assert.Equal(220.1, proto.Metrics["voltage"]);
        Assert.Equal(12.5, proto.Metrics["activeEnergy"]);
    }

    [Fact]
    public void ToProto_CurrentFreshness_MapsToCurrentString()
    {
        var proto = ReadingMapper.ToProto(Reading(TelemetryFreshness.Current));

        Assert.Equal("current", proto.Freshness);
    }

    [Fact]
    public void ToProto_DelayedFreshness_MapsToDelayedString()
    {
        var proto = ReadingMapper.ToProto(Reading(TelemetryFreshness.Delayed));

        Assert.Equal("delayed", proto.Freshness);
    }

    [Fact]
    public void ToProto_NullReading_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ReadingMapper.ToProto(null!));
    }
}
