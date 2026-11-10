using Smartgn.Telemetry.Domain;

namespace Smartgn.Telemetry.Tests;

public class TelemetryPayloadParserTests
{
    private static string ValidPayload(
        int schemaVersion = 1,
        string deviceId = "meter-001",
        string eventId = "evt-0001",
        long sequenceNumber = 1,
        string measuredAt = "2026-05-10T12:00:00Z",
        long associationVersion = 3,
        string metrics = """{"activeEnergy": 12.5, "voltage": 220.1}""") =>
        $$"""
          {
            "schemaVersion": {{schemaVersion}},
            "deviceId": "{{deviceId}}",
            "eventId": "{{eventId}}",
            "sequenceNumber": {{sequenceNumber}},
            "measuredAt": "{{measuredAt}}",
            "associationVersion": {{associationVersion}},
            "metrics": {{metrics}}
          }
          """;

    [Fact]
    public void Parse_ValidPayload_ReturnsNormalizedEvent()
    {
        var result = TelemetryPayloadParser.Parse(ValidPayload());

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        var telemetryEvent = Assert.IsType<TelemetryEvent>(result.Value);
        Assert.Equal("meter-001", telemetryEvent.DeviceId);
        Assert.Equal("evt-0001", telemetryEvent.EventId);
        Assert.Equal(1, telemetryEvent.SequenceNumber);
        Assert.Equal(3, telemetryEvent.AssociationVersion);
        Assert.Equal(2, telemetryEvent.Metrics.Count);
        Assert.Equal(12.5, telemetryEvent.Metrics["activeEnergy"]);
        Assert.Equal(220.1, telemetryEvent.Metrics["voltage"]);
        Assert.Equal(DateTimeOffset.Parse("2026-05-10T12:00:00Z"), telemetryEvent.MeasuredAt);
    }

    [Theory]
    [InlineData(null, "payload_required")]
    [InlineData("", "payload_required")]
    [InlineData("not-json", "invalid_json")]
    [InlineData("[]", "invalid_json")]
    public void Parse_MalformedPayload_FailsWithStableError(string? payload, string expectedError)
    {
        var result = TelemetryPayloadParser.Parse(payload!);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public void Parse_UnsupportedSchemaVersion_Fails()
    {
        var result = TelemetryPayloadParser.Parse(ValidPayload(schemaVersion: 2));

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported_schema_version", result.Error);
    }

    [Fact]
    public void Parse_MissingSchemaVersion_Fails()
    {
        var payload = """{"deviceId":"meter-001","eventId":"evt-1","sequenceNumber":1,"measuredAt":"2026-05-10T12:00:00Z","metrics":{"voltage":220}}""";

        var result = TelemetryPayloadParser.Parse(payload);

        Assert.False(result.IsSuccess);
        Assert.Equal("schema_version_required", result.Error);
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("meter/../etc")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_InvalidDeviceId_Fails(string? deviceId)
    {
        var result = TelemetryPayloadParser.Parse(ValidPayload(deviceId: deviceId ?? ""));

        Assert.False(result.IsSuccess);
        Assert.StartsWith("deviceId_", result.Error);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("\"abc\"")]
    [InlineData("1.5")]
    public void Parse_InvalidSequenceNumber_Fails(string rawSequence)
    {
        var payload =
            "{\"schemaVersion\":1,\"deviceId\":\"meter-001\",\"eventId\":\"evt-1\"," +
            "\"sequenceNumber\":" + rawSequence +
            ",\"measuredAt\":\"2026-05-10T12:00:00Z\",\"metrics\":{\"voltage\":220}}";

        var result = TelemetryPayloadParser.Parse(payload);

        Assert.False(result.IsSuccess);
        Assert.Equal("sequence_number_invalid", result.Error);
    }

    [Fact]
    public void Parse_InvalidMeasuredAt_Fails()
    {
        var result = TelemetryPayloadParser.Parse(ValidPayload(measuredAt: "yesterday"));

        Assert.False(result.IsSuccess);
        Assert.Equal("measured_at_invalid", result.Error);
    }

    [Fact]
    public void Parse_EmptyMetrics_Fails()
    {
        var result = TelemetryPayloadParser.Parse(ValidPayload(metrics: "{}"));

        Assert.False(result.IsSuccess);
        Assert.Equal("metrics_required", result.Error);
    }

    [Fact]
    public void Parse_MetricNameWithInvalidCharacters_Fails()
    {
        var result = TelemetryPayloadParser.Parse(ValidPayload(metrics: """{"active energy": 1}"""));

        Assert.False(result.IsSuccess);
        Assert.Equal("metric_name_invalid", result.Error);
    }

    [Fact]
    public void Parse_NonFiniteMetricValue_Fails()
    {
        var result = TelemetryPayloadParser.Parse(ValidPayload(metrics: """{"voltage": 1e999}"""));

        Assert.False(result.IsSuccess);
        Assert.Equal("metric_value_invalid", result.Error);
    }

    [Fact]
    public void Parse_MissingMetrics_Fails()
    {
        var payload = """{"schemaVersion":1,"deviceId":"meter-001","eventId":"evt-1","sequenceNumber":1,"measuredAt":"2026-05-10T12:00:00Z"}""";

        var result = TelemetryPayloadParser.Parse(payload);

        Assert.False(result.IsSuccess);
        Assert.Equal("metrics_required", result.Error);
    }

    [Fact]
    public void Parse_OptionalAssociationVersion_DefaultsToZero()
    {
        var payload = """{"schemaVersion":1,"deviceId":"meter-001","eventId":"evt-1","sequenceNumber":1,"measuredAt":"2026-05-10T12:00:00Z","metrics":{"voltage":220}}""";

        var result = TelemetryPayloadParser.Parse(payload);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value!.AssociationVersion);
    }
}
