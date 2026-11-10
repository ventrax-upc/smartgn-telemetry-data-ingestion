using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Grpc;

/// <summary>Maps stored readings onto the private protobuf contract.</summary>
public static class ReadingMapper
{
    public static Reading ToProto(TelemetryReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        var proto = new Reading
        {
            DeviceId = reading.DeviceId,
            EventId = reading.EventId,
            SequenceNumber = reading.SequenceNumber,
            MeasuredAtUnixMs = reading.MeasuredAt.ToUnixTimeMilliseconds(),
            ReceivedAtUnixMs = reading.ReceivedAt.ToUnixTimeMilliseconds(),
            Freshness = reading.Freshness == TelemetryFreshness.Delayed ? "delayed" : "current",
            AssociationVersion = reading.AssociationVersion,
        };

        foreach (var (name, value) in reading.Metrics)
        {
            proto.Metrics[name] = value;
        }

        return proto;
    }
}
